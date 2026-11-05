using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PowerSwitch
{
    /// <summary>
    /// 引擎主控选举。
    ///
    /// 背景：看门狗是「每个登录会话各拉一个实例」，这样每个用户都能看到托盘图标。
    /// 但电源方案是全机唯一的系统状态，多个实例同时驱动会互相覆盖 ——
    /// 防抖计时存在各自内存里，阈值附近可能出现一个要降档、一个要保档的来回横跳。
    ///
    /// 所以用一把机器级的互斥量来选出一个「驱动者」：
    ///   · 抢到 Global\PowerSwitchEngineDriver 的实例才真正调用 SetActiveScheme
    ///   · 其余实例进入只读模式：照常采样、照常显示状态，但不改方案
    ///
    /// 跨用户的细节：Global\ 对象默认只有创建者能打开，其他用户的实例来抢会拿到
    /// UnauthorizedAccessException。这个异常恰好就是「别人已经占着」的信号 ——
    /// 因为对象不存在时创建一定会成功。所以不需要给互斥量配 DACL。
    ///
    /// 驱动者退出（进程结束 / 会话注销）后互斥量自动销毁，其余实例下一轮就能抢到。
    /// </summary>
    internal static class EngineLeader
    {
        private const string MutexName = @"Global\PowerSwitchEngineDriver";
        private const string LeaderFile = "engine.leader";

        private static readonly object Sync = new object();
        private static Mutex _mutex;
        private static bool _isLeader;
        private static int _leaderSession = -1;

        // 能否写数据目录（日志/状态文件）—— 用来预判「这个实例有没有资格驱动」。
        // 多用户共用 ProgramData 时，非属主账户只有读权限，它既写不了日志，
        // 往往也没有改电源方案的权限（实测 home 账户会拿到 0x5 ACCESS_DENIED）。
        private static bool _probed;
        private static bool _canWrite = true;
        private static readonly DateTime StartedAt = DateTime.Now;
        private static readonly TimeSpan WriteGrace = TimeSpan.FromSeconds(15);

        // 一旦实测发现自己改不了方案，本进程就不再参与选举，避免两个实例反复抢
        private static bool _cannotDrive;

        // 节流：2 秒的请求泵会反复调用 TryAcquire/Heartbeat，没必要每次都碰内核对象和磁盘
        private static DateTime _lastAttempt = DateTime.MinValue;
        private static DateTime _lastBeat = DateTime.MinValue;
        private static readonly TimeSpan AttemptInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan BeatInterval = TimeSpan.FromSeconds(30);

        public static bool IsLeader
        {
            get { lock (Sync) { return _isLeader; } }
        }

        /// <summary>驱动者所在的会话号，未知时为 -1。</summary>
        public static int LeaderSession
        {
            get { lock (Sync) { return _leaderSession; } }
        }

        /// <summary>
        /// 尝试成为驱动者。已经是驱动者时直接返回 true。
        /// 返回 true 表示「本次调用刚刚抢到」，调用方可以据此打一条日志。
        /// </summary>
        public static bool TryAcquire()
        {
            lock (Sync)
            {
                if (_isLeader) { return false; }
                if (_cannotDrive) { return false; }

                if ((DateTime.Now - _lastAttempt) < AttemptInterval) { return false; }
                _lastAttempt = DateTime.Now;

                // 能写数据目录的实例优先：写不了日志的实例通常也改不了电源方案，
                // 让它当驱动者等于让整个工具失效。给能写的实例 15 秒先手时间，
                // 过了这段时间还是没人上位，写不了的实例再兜底 —— 避免无人驱动。
                if (!_probed)
                {
                    _canWrite = ProbeWritable();
                    _probed = true;
                    if (!_canWrite)
                    {
                        Logger.Warn("数据目录不可写，本次不主动竞选驱动者（15 秒后若仍无人驱动则兜底）");
                    }
                }
                if (!_canWrite && (DateTime.Now - StartedAt) < WriteGrace)
                {
                    return false;
                }

                try
                {
                    bool createdNew;
                    Mutex m = new Mutex(true, MutexName, out createdNew);
                    if (!createdNew)
                    {
                        // 对象已存在：说明别的会话正在驱动（或者是同进程内重复调用）
                        m.Dispose();
                        ReadLeaderFile();
                        return false;
                    }

                    _mutex = m;
                    _isLeader = true;
                    _leaderSession = SafeSessionId();
                    WriteLeaderFile();
                    Logger.Info("本会话成为电源方案驱动者（会话 " + _leaderSession + "）");
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    // 对象存在但不属于当前用户 —— 另一个会话的实例占着，正常情况
                    ReadLeaderFile();
                    return false;
                }
                catch (Exception ex)
                {
                    Logger.Warn("引擎主控选举失败，本轮按只读处理: " + Logger.Describe(ex));
                    return false;
                }
            }
        }

        /// <summary>能否往数据目录写文件（以日志为探针，因为它是多实例共写的）。</summary>
        private static bool ProbeWritable()
        {
            try
            {
                string p = Path.Combine(Paths.DataDir, "power_switch.log");
                using (FileStream fs = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    return fs.CanWrite;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 实测发现自己改不了电源方案时调用（例如 PowerSetActiveScheme 返回 0x5）。
        /// 让位并永久退出选举，交给其他会话的实例。
        /// </summary>
        public static void ReportCannotDrive(string reason)
        {
            lock (Sync)
            {
                if (_cannotDrive) { return; }
                _cannotDrive = true;
                Logger.Warn("本实例无法驱动电源方案（" + reason + "），让位给其他会话");
                Release();
            }
        }

        /// <summary>驱动者主动让位（进程退出时调用，主要是为了让日志清楚）。</summary>
        public static void Release()
        {
            lock (Sync)
            {
                if (!_isLeader) { return; }
                try
                {
                    if (_mutex != null)
                    {
                        _mutex.ReleaseMutex();
                        _mutex.Dispose();
                    }
                }
                catch
                {
                }
                _mutex = null;
                _isLeader = false;
                _leaderSession = -1;
                try
                {
                    string f = Path.Combine(Paths.DataDir, LeaderFile);
                    if (File.Exists(f)) { File.Delete(f); }
                }
                catch
                {
                }
                Logger.Info("已让出电源方案驱动权");
            }
        }

        /// <summary>
        /// 驱动者每轮心跳时刷新一次，让只读实例能显示「由会话 N 驱动」。
        /// </summary>
        public static void Heartbeat()
        {
            lock (Sync)
            {
                if (!_isLeader) { return; }
                if ((DateTime.Now - _lastBeat) < BeatInterval) { return; }
                _lastBeat = DateTime.Now;
                _leaderSession = SafeSessionId();
                WriteLeaderFile();
            }
        }

        private static void WriteLeaderFile()
        {
            try
            {
                string f = Path.Combine(Paths.DataDir, LeaderFile);
                File.WriteAllText(f,
                    Process.GetCurrentProcess().Id + "|" + _leaderSession + "|" +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch
            {
                // 只读实例拿不到会话号只是显示问题，不影响控制权
            }
        }

        private static void ReadLeaderFile()
        {
            try
            {
                string f = Path.Combine(Paths.DataDir, LeaderFile);
                if (!File.Exists(f)) { _leaderSession = -1; return; }
                string text = File.ReadAllText(f).Trim();
                string[] parts = text.Split('|');
                if (parts.Length >= 2)
                {
                    int sid;
                    if (int.TryParse(parts[1], out sid)) { _leaderSession = sid; }
                }
            }
            catch
            {
                _leaderSession = -1;
            }
        }

        private static int SafeSessionId()
        {
            try { return Process.GetCurrentProcess().SessionId; }
            catch { return -1; }
        }
    }
}
