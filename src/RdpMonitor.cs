using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Threading.Timer;

namespace PowerSwitch
{
    /// <summary>一次探测得到的远程桌面会话信息。</summary>
    internal sealed class RdpSessionInfo
    {
        public bool Active;
        public int SessionId;
        public string ClientName;
        public string UserName;

        public string Describe()
        {
            if (!Active) { return "无"; }
            string who = string.IsNullOrEmpty(UserName) ? "未知用户" : UserName;
            string from = string.IsNullOrEmpty(ClientName) ? "未知客户端" : ClientName;
            return who + "@" + from + "（会话 " + SessionId + "）";
        }
    }

    /// <summary>
    /// 远程桌面（RDP）连接状态监控。
    ///
    /// 判定依据不是"有没有 mstsc 进程"——那只说明本机往外连；我们要的是**别人连进来**。
    /// 所以直接问终端服务：枚举本机所有会话，凡是「状态 = 活动 且 客户端协议 = RDP」的，
    /// 就是一条正在使用的远程桌面连接。
    ///
    /// 两条通道并行，互为兜底：
    ///   1. WTSRegisterSessionNotification —— 会话变化时系统主动发消息，延迟约 0.4 秒
    ///   2. 定时轮询 —— 防漏（进程启动前就已连上、消息没收到、跨会话通知不到）
    /// </summary>
    internal sealed class RdpMonitor : IDisposable
    {
        private const int WTSActive = 0;            // WTS_CONNECTSTATE_CLASS
        private const int WTSClientProtocolType = 16;
        private const int WTSUserName = 5;
        private const int WTSClientName = 10;
        private const int ProtocolRdp = 2;          // 0=Console 1=Legacy(ICA) 2=RDP

        private const int WM_WTSSESSION_CHANGE = 0x02B1;
        private const int NOTIFY_FOR_ALL_SESSIONS = 1;

        private readonly object _sync = new object();
        private readonly SessionWatcher _window;
        private Timer _timer;
        private int _pollSeconds;
        private int _evaluating;
        private bool _disposed;

        private RdpSessionInfo _current = new RdpSessionInfo();
        private bool _notifyRegistered;

        public RdpMonitor(int pollSeconds)
        {
            _pollSeconds = ClampPoll(pollSeconds);
            _window = new SessionWatcher(this);
            _timer = new Timer(OnTimer, null, 400, _pollSeconds * 1000);

            // 启动即探测一次：程序是在 RDP 会话里被拉起来的时候，不能等到下一次轮询才发现
            ThreadPool.QueueUserWorkItem(delegate { Evaluate(); });
        }

        /// <summary>远程桌面状态发生变化时触发（在后台线程上）。</summary>
        public event EventHandler Changed;

        public RdpSessionInfo Current
        {
            get { lock (_sync) { return _current; } }
        }

        public bool IsRemoteActive
        {
            get { lock (_sync) { return _current.Active; } }
        }

        public int PollSeconds
        {
            get { lock (_sync) { return _pollSeconds; } }
        }

        public void SetPollInterval(int seconds)
        {
            int v = ClampPoll(seconds);
            lock (_sync)
            {
                if (v == _pollSeconds) { return; }
                _pollSeconds = v;
            }
            if (_disposed) { return; }
            try
            {
                if (_timer != null) { _timer.Dispose(); }
                _timer = new Timer(OnTimer, null, 1000, v * 1000);
                Logger.Info("远程桌面检测间隔改为 " + v + " 秒");
            }
            catch (Exception ex)
            {
                Logger.Warn("调整远程桌面检测间隔失败: " + Logger.Describe(ex));
            }
        }

        private static int ClampPoll(int seconds)
        {
            if (seconds < 1) { return 1; }
            if (seconds > 60) { return 60; }
            return seconds;
        }

        private void OnTimer(object state)
        {
            Evaluate();
        }

        /// <summary>
        /// 探测并比对。只有"是否处于远程会话"这个布尔值变化（或连接换了一条）才通知，
        /// 避免每秒都往日志和托盘里刷同一条状态。
        /// </summary>
        public void Evaluate()
        {
            if (_disposed) { return; }
            if (Interlocked.Exchange(ref _evaluating, 1) == 1) { return; }
            try
            {
                RdpSessionInfo info = Probe();
                bool changed;
                lock (_sync)
                {
                    changed = info.Active != _current.Active
                        || (info.Active && (info.SessionId != _current.SessionId
                                            || !string.Equals(info.ClientName, _current.ClientName, StringComparison.OrdinalIgnoreCase)));
                    _current = info;
                }

                if (!changed) { return; }
                Logger.Info("远程桌面状态变化: " + (info.Active ? "已连接 " + info.Describe() : "已断开"));
                RaiseChanged();
            }
            catch (Exception ex)
            {
                Logger.Warn("远程桌面探测异常: " + Logger.Describe(ex));
            }
            finally
            {
                Interlocked.Exchange(ref _evaluating, 0);
            }
        }

        /// <summary>会话消息比状态生效早一点点，延迟一次再读，避免读到过渡态。</summary>
        private void ScheduleEvaluate(int delayMs)
        {
            if (_disposed) { return; }
            try
            {
                Timer one = null;
                one = new Timer(delegate
                {
                    try { one.Dispose(); } catch { }
                    Evaluate();
                }, null, delayMs, Timeout.Infinite);
            }
            catch
            {
            }
        }

        private void OnSessionChange()
        {
            // 先快后慢各读一次：连接建立时新会话可能还没进入 Active
            ScheduleEvaluate(400);
            ScheduleEvaluate(1500);
        }

        private void RaiseChanged()
        {
            EventHandler h = Changed;
            if (h == null) { return; }
            try { h(this, EventArgs.Empty); }
            catch (Exception ex) { Logger.Warn("远程桌面状态回调异常: " + Logger.Describe(ex)); }
        }

        // ---------------------------------------------------------------- 探测

        /// <summary>当前是否存在活动的 RDP 会话。任何异常都按"未连接"处理，不影响主功能。</summary>
        public static RdpSessionInfo Probe()
        {
            RdpSessionInfo result = new RdpSessionInfo();
            IntPtr buffer = IntPtr.Zero;
            int count = 0;
            try
            {
                if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out buffer, out count) || buffer == IntPtr.Zero)
                {
                    return result;
                }

                int size = Marshal.SizeOf(typeof(WtsSessionInfo));
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(buffer.ToInt64() + (long)i * size);
                    WtsSessionInfo si = (WtsSessionInfo)Marshal.PtrToStructure(item, typeof(WtsSessionInfo));

                    if (si.State != WTSActive) { continue; }
                    if (QueryProtocol(si.SessionId) != ProtocolRdp) { continue; }

                    result.Active = true;
                    result.SessionId = si.SessionId;
                    result.ClientName = QueryString(si.SessionId, WTSClientName);
                    result.UserName = QueryString(si.SessionId, WTSUserName);
                    return result;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("枚举会话失败: " + Logger.Describe(ex));
            }
            finally
            {
                if (buffer != IntPtr.Zero) { WTSFreeMemory(buffer); }
            }
            return result;
        }

        private static int QueryProtocol(int sessionId)
        {
            IntPtr buf = IntPtr.Zero;
            int bytes = 0;
            try
            {
                if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WTSClientProtocolType, out buf, out bytes))
                {
                    return -1;
                }
                if (buf == IntPtr.Zero || bytes < 2) { return -1; }
                // 该信息项返回的是 USHORT，不是 DWORD
                return Marshal.ReadInt16(buf);
            }
            catch
            {
                return -1;
            }
            finally
            {
                if (buf != IntPtr.Zero) { WTSFreeMemory(buf); }
            }
        }

        private static string QueryString(int sessionId, int infoClass)
        {
            IntPtr buf = IntPtr.Zero;
            int bytes = 0;
            try
            {
                if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out buf, out bytes))
                {
                    return null;
                }
                if (buf == IntPtr.Zero || bytes < 2) { return null; }
                return Marshal.PtrToStringUni(buf);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (buf != IntPtr.Zero) { WTSFreeMemory(buf); }
            }
        }

        // ---------------------------------------------------------------- 清理

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            try
            {
                if (_timer != null) { _timer.Dispose(); _timer = null; }
            }
            catch
            {
            }
            try
            {
                if (_window != null) { _window.Detach(); }
            }
            catch
            {
            }
        }

        // ---------------------------------------------------------------- P/Invoke

        [StructLayout(LayoutKind.Sequential)]
        private struct WtsSessionInfo
        {
            public int SessionId;
            [MarshalAs(UnmanagedType.LPWStr)] public string WinStationName;
            public int State;
        }

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSEnumerateSessionsW")]
        private static extern bool WTSEnumerateSessionsW(IntPtr hServer, int reserved, int version, out IntPtr sessionInfo, out int count);

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW")]
        private static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int flags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        /// <summary>只为了收 WM_WTSSESSION_CHANGE 而存在的隐藏窗口。</summary>
        private sealed class SessionWatcher : NativeWindow
        {
            private readonly RdpMonitor _owner;

            public SessionWatcher(RdpMonitor owner)
            {
                _owner = owner;
                try
                {
                    CreateParams cp = new CreateParams();
                    cp.Caption = "PowerSwitchSessionWatcher";
                    cp.Style = 0;
                    cp.ExStyle = 0;
                    CreateHandle(cp);

                    _owner._notifyRegistered = WTSRegisterSessionNotification(Handle, NOTIFY_FOR_ALL_SESSIONS);
                    if (!_owner._notifyRegistered)
                    {
                        Logger.Warn("注册会话变更通知失败（错误码 " + Marshal.GetLastWin32Error() + "），仅靠轮询检测远程桌面");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("创建会话监听窗口失败: " + Logger.Describe(ex));
                }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_WTSSESSION_CHANGE)
                {
                    try { _owner.OnSessionChange(); }
                    catch (Exception ex) { Logger.Warn("处理会话变更消息失败: " + Logger.Describe(ex)); }
                }
                base.WndProc(ref m);
            }

            public void Detach()
            {
                try
                {
                    if (_owner._notifyRegistered)
                    {
                        WTSUnRegisterSessionNotification(Handle);
                        _owner._notifyRegistered = false;
                    }
                }
                catch
                {
                }
                try { DestroyHandle(); }
                catch { }
            }
        }
    }
}
