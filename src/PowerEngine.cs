using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace PowerSwitch
{
    /// <summary>
    /// 电源方案决策引擎。行为与旧 PowerShell 版 SmartPowerSwitch.ps1 完全对齐：
    ///
    ///   优先级 0（最高）：远程桌面连接中 → 锁定 RemoteDesktopMode，不做任何自动切换
    ///   优先级 1：手动暂停标志 → 什么都不做
    ///   优先级 2：自动逻辑
    ///        useLoadLogic = AllDayMonitor || 处于夜间时段
    ///        ├─ false（白天且未开全天监控）→ 固定 DayMode
    ///        └─ true → 按 CPU 负载走防抖状态机
    ///             ├─ 低负载持续 LowLoadDuration 分钟 → NightLowLoadMode
    ///             └─ 高负载持续 HighLoadDuration 分钟 → NightHighLoadMode
    ///
    /// 区别只在于：这里跑在常驻进程的定时器里，不再每分钟起一个新进程。
    /// </summary>
    internal sealed class PowerEngine
    {
        private readonly string _configPath;
        private readonly string _statePath;
        private readonly string _pausePath;
        private readonly object _sync = new object();

        private AppConfig _cfg;
        private DateTime? _lowSince;
        private DateTime? _highSince;
        private string _lastLoadState;

        private string _currentKey;
        private double _lastCpu = -1;
        private DateTime _lastTick = DateTime.MinValue;
        private string _lastAction = "尚未运行";
        private DateTime _lastSummary = DateTime.MinValue;

        // 远程桌面覆盖：外部机器连进来时锁定方案，断开后恢复自动切换
        private bool _rdpActive;
        private string _rdpClient;
        private DateTime? _rdpSince;

        public PowerEngine(string baseDir)
        {
            _configPath = Path.Combine(baseDir, "power_config.json");
            _statePath = Path.Combine(baseDir, "power_switch_state.json");
            _pausePath = Path.Combine(baseDir, "automation.paused");
            _cfg = ConfigStore.Load(_configPath);
            LoadState();
        }

        public event EventHandler Changed;

        public string ConfigPath { get { return _configPath; } }
        public string PausePath { get { return _pausePath; } }

        public AppConfig Config
        {
            get { lock (_sync) { return _cfg.Clone(); } }
        }

        public string CurrentKey
        {
            get
            {
                lock (_sync)
                {
                    if (_currentKey == null) { RefreshCurrent(); }
                    return _currentKey;
                }
            }
        }

        public string LastAction
        {
            get { lock (_sync) { return _lastAction; } }
        }

        public double LastCpu
        {
            get { lock (_sync) { return _lastCpu; } }
        }

        public DateTime LastTick
        {
            get { lock (_sync) { return _lastTick; } }
        }

        /// <summary>当前负载判定："Low" / "High" / null（还没采样或已清空）。</summary>
        public string CurrentLoadState
        {
            get { lock (_sync) { return _lastLoadState; } }
        }

        public DateTime? LowSince
        {
            get { lock (_sync) { return _lowSince; } }
        }

        public DateTime? HighSince
        {
            get { lock (_sync) { return _highSince; } }
        }

        public bool IsPaused
        {
            get { return File.Exists(_pausePath); }
        }

        /// <summary>是否处于远程桌面连接中（原始事实，不受配置开关影响）。</summary>
        public bool IsRemoteDesktopActive
        {
            get { lock (_sync) { return _rdpActive; } }
        }

        /// <summary>远程桌面覆盖当前是否生效（连接中 + 配置里开着联动）。</summary>
        public bool IsRemoteOverrideActive
        {
            get { lock (_sync) { return _rdpActive && _cfg.PauseOnRemoteDesktop; } }
        }

        public string RemoteDesktopClient
        {
            get { lock (_sync) { return _rdpClient; } }
        }

        public DateTime? RemoteDesktopSince
        {
            get { lock (_sync) { return _rdpSince; } }
        }

        public string PauseUntilText
        {
            get
            {
                try
                {
                    if (!File.Exists(_pausePath)) { return null; }
                    string text = File.ReadAllText(_pausePath, Encoding.UTF8);
                    if (text.IndexOf("\"until\"", StringComparison.OrdinalIgnoreCase) < 0) { return null; }
                    // 格式：{"until":"2026-11-03T23:00:00"}
                    int start = text.IndexOf("until", StringComparison.OrdinalIgnoreCase);
                    start = text.IndexOf('"', start + 5);
                    if (start < 0) { return null; }
                    int end = text.IndexOf('"', start + 1);
                    if (end < 0) { return null; }
                    string raw = text.Substring(start + 1, end - start - 1);
                    DateTime until;
                    if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out until))
                    {
                        return until.ToString("MM-dd HH:mm");
                    }
                }
                catch
                {
                }
                return null;
            }
        }

        public void ReloadConfig()
        {
            string rdpTarget = null;
            lock (_sync)
            {
                _cfg = ConfigStore.Load(_configPath);
                Logger.Info("配置已重新载入: " + _configPath);
                // 改完设置时远程桌面可能正连着：开关刚打开、或目标方案被换掉，都要立刻补一次
                if (_rdpActive && _cfg.PauseOnRemoteDesktop) { rdpTarget = _cfg.RemoteDesktopMode; }
            }
            if (rdpTarget != null)
            {
                Logger.Info("配置变更时远程桌面仍在连接中，重新锁定 " + PowerScheme.DisplayName(rdpTarget));
                ApplyScheme(rdpTarget, "远程桌面锁定");
            }
            RaiseChanged();
        }

        /// <summary>
        /// 由 RdpMonitor 调用。连接建立时立刻切到指定方案并冻结自动切换，
        /// 断开时清空防抖计时，让下一次 Tick 重新按负载判断。
        /// </summary>
        public void SetRemoteDesktopActive(bool active, string client)
        {
            bool changed;
            lock (_sync)
            {
                changed = _rdpActive != active;
                _rdpActive = active;
                _rdpClient = client;
                if (changed) { _rdpSince = active ? DateTime.Now : (DateTime?)null; }
            }
            if (!changed) { return; }

            AppConfig cfg = Config;

            if (!active)
            {
                Logger.Info("远程桌面已断开，恢复自动切换");
                lock (_sync)
                {
                    ClearStreaks();
                    SetAction("远程桌面已断开，恢复自动切换");
                }
                RaiseChanged();
                return;
            }

            if (!cfg.PauseOnRemoteDesktop)
            {
                Logger.Info("检测到远程桌面连接（" + client + "），但联动已关闭，不做处理");
                RaiseChanged();
                return;
            }

            Logger.Info("检测到远程桌面连接：" + client + "，立即切换到 "
                        + PowerScheme.DisplayName(cfg.RemoteDesktopMode) + " 并暂停自动切换");
            lock (_sync) { ClearStreaks(); }
            ApplyScheme(cfg.RemoteDesktopMode, "远程桌面连接");
            SaveState();
            RaiseChanged();
        }

        /// <summary>暂停自动切换。duration 为 null 表示一直暂停到手动恢复。</summary>
        public void Pause(TimeSpan? duration)
        {
            try
            {
                string dir = Path.GetDirectoryName(_pausePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }

                if (duration.HasValue)
                {
                    DateTime until = DateTime.Now.Add(duration.Value);
                    string json = "{\"until\":\"" + until.ToString("yyyy-MM-ddTHH:mm:ss") + "\"}";
                    File.WriteAllText(_pausePath, json, new UTF8Encoding(true));
                    Logger.Info("已暂停自动切换，直到 " + until.ToString("yyyy-MM-dd HH:mm"));
                    SetAction("已暂停到 " + until.ToString("HH:mm"));
                }
                else
                {
                    File.WriteAllText(_pausePath, "{\"until\":null}", new UTF8Encoding(true));
                    Logger.Info("已暂停自动切换，直到手动恢复");
                    SetAction("已暂停（手动恢复）");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("写入暂停标志失败", ex);
            }
            RaiseChanged();
        }

        public void Resume()
        {
            try
            {
                if (File.Exists(_pausePath)) { File.Delete(_pausePath); }
                Logger.Info("已恢复自动切换");
                SetAction("自动切换已恢复");
            }
            catch (Exception ex)
            {
                Logger.Error("移除暂停标志失败", ex);
            }
            RaiseChanged();
        }

        /// <summary>手动指定方案。会同时清空防抖计时，避免下一秒又被自动逻辑弹回去。</summary>
        public bool ForceSwitch(string schemeKey)
        {
            string key = PowerScheme.NormalizeName(schemeKey, null);
            if (key == null) { return false; }

            bool ok = PowerNative.SetActiveScheme(PowerScheme.ToGuid(key));
            if (ok)
            {
                lock (_sync)
                {
                    _currentKey = key;
                    _lowSince = null;
                    _highSince = null;
                    _lastLoadState = null;
                    SetAction("手动切换到 " + PowerScheme.DisplayName(key));
                }
                SaveState();
                Logger.Info("手动切换电源方案 -> " + PowerScheme.DisplayName(key));
                RaiseChanged();
            }
            return ok;
        }

        private void SetAction(string text)
        {
            _lastAction = text;
        }

        private void RefreshCurrent()
        {
            Guid? g = PowerNative.GetActiveScheme();
            if (g.HasValue)
            {
                string key = PowerScheme.FromGuid(g.Value);
                _currentKey = key != null ? key : g.Value.ToString();
            }
        }

        /// <summary>
        /// 主循环体。由定时器在后台线程调用，内部吞掉所有异常 ——
        /// 任何一次采样失败都不应该让托盘进程退出。
        /// </summary>
        public void Tick()
        {
            try
            {
                TickCore();
            }
            catch (Exception ex)
            {
                Logger.Error("Tick 未处理异常", ex);
                SetAction("运行异常，详见日志");
            }
            finally
            {
                RaiseChanged();
            }
        }

        private void TickCore()
        {
            AppConfig cfg;
            lock (_sync) { cfg = _cfg.Clone(); }

            // 暂停标志：到期自动恢复
            bool paused = File.Exists(_pausePath);
            DateTime? pauseUntil = null;
            if (paused)
            {
                pauseUntil = ReadPauseUntil();
                if (pauseUntil.HasValue && DateTime.Now >= pauseUntil.Value)
                {
                    try { File.Delete(_pausePath); } catch { }
                    Logger.Info("暂停已到期，恢复自动切换");
                    paused = false;
                    pauseUntil = null;
                }
            }

            RefreshCurrent();
            string current = _currentKey;

            // 远程桌面优先级最高：连接期间只认 RemoteDesktopMode，连手动暂停也要让位
            // —— 远程操作时被降到节能档会让整台机器明显卡顿，这是比"保持手动选择"更强的约束。
            if (_rdpActive && cfg.PauseOnRemoteDesktop)
            {
                // 锁定期间照样采一次 CPU：设置窗口的状态条要用它，
                // 不采的话会一直显示 "--"，看起来像程序卡住了。
                double rdCpu = SampleAverage(cfg);
                if (rdCpu >= 0) { _lastCpu = rdCpu; }

                if (current != cfg.RemoteDesktopMode)
                {
                    Logger.Info("远程桌面连接中 - 强制切换到 " + PowerScheme.DisplayName(cfg.RemoteDesktopMode));
                    ApplyScheme(cfg.RemoteDesktopMode, "远程桌面锁定");
                }
                else
                {
                    SetAction("远程桌面已连接 - 锁定 " + PowerScheme.DisplayName(cfg.RemoteDesktopMode));
                }
                ClearStreaks();
                _lastTick = DateTime.Now;
                SaveState();
                return;
            }

            if (paused)
            {
                SetAction(pauseUntil.HasValue ? "已暂停到 " + pauseUntil.Value.ToString("HH:mm") : "已暂停（手动恢复）");
                _lastTick = DateTime.Now;
                return;
            }

            bool isNight = IsNightTime(cfg, DateTime.Now);
            bool useLoadLogic = cfg.AllDayMonitor || isNight;

            if (!useLoadLogic)
            {
                // 白天且未开全天监控：固定 DayMode
                if (current != cfg.DayMode)
                {
                    Logger.Info("白天 - 强制切换到 " + PowerScheme.DisplayName(cfg.DayMode));
                    ApplyScheme(cfg.DayMode, "白天固定方案");
                }
                else
                {
                    SetAction("白天 - 保持 " + PowerScheme.DisplayName(cfg.DayMode));
                }
                ClearStreaks();
                _lastTick = DateTime.Now;
                SaveState();
                return;
            }

            // 负载分支
            double cpu = SampleAverage(cfg);
            if (cpu < 0)
            {
                SetAction("CPU 采样失败");
                _lastTick = DateTime.Now;
                return;
            }

            bool isLow = cpu < cfg.CpuThreshold;
            DateTime now = DateTime.Now;

            if (isLow)
            {
                if (_lastLoadState != "Low")
                {
                    _lowSince = now;
                    _highSince = null;
                    _lastLoadState = "Low";
                    Logger.Info(string.Format("进入低负载状态，开始计时（需 {0} 分钟）", cfg.LowLoadDuration));
                    SetAction("低负载计时中 0/" + cfg.LowLoadDuration + " 分钟");
                }
                else
                {
                    double elapsed = _lowSince.HasValue ? (now - _lowSince.Value).TotalMinutes : 0;
                    if (elapsed >= cfg.LowLoadDuration && current != cfg.NightLowLoadMode)
                    {
                        Logger.Info(string.Format("低负载已持续 {0:F1} 分钟，切换到 {1}", elapsed, PowerScheme.DisplayName(cfg.NightLowLoadMode)));
                        ApplyScheme(cfg.NightLowLoadMode, "低负载降档");
                    }
                    else if (current == cfg.NightLowLoadMode)
                    {
                        SetAction("低负载 - 保持 " + PowerScheme.DisplayName(cfg.NightLowLoadMode));
                    }
                    else
                    {
                        SetAction(string.Format("低负载 {0:F0}/{1} 分钟", elapsed, cfg.LowLoadDuration));
                    }
                }
            }
            else
            {
                if (_lastLoadState != "High")
                {
                    _highSince = now;
                    _lowSince = null;
                    _lastLoadState = "High";
                    Logger.Info(string.Format("进入高负载状态，开始计时（需 {0} 分钟）", cfg.HighLoadDuration));
                    SetAction("高负载计时中 0/" + cfg.HighLoadDuration + " 分钟");
                }
                else
                {
                    double elapsed = _highSince.HasValue ? (now - _highSince.Value).TotalMinutes : 0;
                    if (elapsed >= cfg.HighLoadDuration && current != cfg.NightHighLoadMode)
                    {
                        Logger.Info(string.Format("高负载已持续 {0:F1} 分钟，切换到 {1}", elapsed, PowerScheme.DisplayName(cfg.NightHighLoadMode)));
                        ApplyScheme(cfg.NightHighLoadMode, "高负载升档");
                    }
                    else if (current == cfg.NightHighLoadMode)
                    {
                        SetAction("高负载 - 保持 " + PowerScheme.DisplayName(cfg.NightHighLoadMode));
                    }
                    else
                    {
                        SetAction(string.Format("高负载 {0:F0}/{1} 分钟", elapsed, cfg.HighLoadDuration));
                    }
                }
            }

            _lastCpu = cpu;
            _lastTick = now;
            MaybeWriteSummary(cfg, cpu, isLow, current);
            SaveState();
        }

        /// <summary>
        /// 每 10 分钟留一行心跳。旧版每分钟写十几行，一天就滚出几 MB；
        /// 完全不写又让人不知道它到底在不在跑，这个频率是折中。
        /// </summary>
        private void MaybeWriteSummary(AppConfig cfg, double cpu, bool isLow, string current)
        {
            if ((DateTime.Now - _lastSummary).TotalMinutes < 10) { return; }
            _lastSummary = DateTime.Now;
            Logger.Info(string.Format(
                "监控中: CPU {0:F1}% (阈值 {1}%) | 负载 {2} | 当前方案 {3} | {4}",
                cpu, cfg.CpuThreshold, isLow ? "低" : "高",
                PowerScheme.DisplayName(current), _lastAction));
        }

        private void ApplyScheme(string key, string reason)
        {
            bool ok = PowerNative.SetActiveScheme(PowerScheme.ToGuid(key));
            if (ok)
            {
                _currentKey = key;
                SetAction(reason + " → " + PowerScheme.DisplayName(key));
            }
            else
            {
                SetAction("切换失败，详见日志");
            }
        }

        private void ClearStreaks()
        {
            _lowSince = null;
            _highSince = null;
            _lastLoadState = null;
        }

        private double SampleAverage(AppConfig cfg)
        {
            int count = Math.Max(1, cfg.SampleCount);
            int intervalMs = Math.Max(1, cfg.SampleIntervalSec) * 1000;
            double sum = 0;
            int ok = 0;
            for (int i = 0; i < count; i++)
            {
                double v = PowerNative.SampleCpuUsage(i == 0 ? 200 : intervalMs);
                if (v >= 0) { sum += v; ok++; }
            }
            if (ok == 0) { return -1; }
            return Math.Round(sum / ok, 1);
        }

        private static bool IsNightTime(AppConfig cfg, DateTime now)
        {
            int hour = now.Hour;
            if (cfg.NightStartHour > cfg.NightEndHour)
            {
                return hour >= cfg.NightStartHour || hour < cfg.NightEndHour;
            }
            return hour >= cfg.NightStartHour && hour < cfg.NightEndHour;
        }

        private DateTime? ReadPauseUntil()
        {
            try
            {
                string text = File.ReadAllText(_pausePath, Encoding.UTF8);
                int idx = text.IndexOf("until", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) { return null; }
                int q1 = text.IndexOf('"', idx);
                if (q1 < 0) { return null; }
                int q2 = text.IndexOf('"', q1 + 1);
                if (q2 < 0) { return null; }
                string raw = text.Substring(q1 + 1, q2 - q1 - 1);
                if (raw == "null" || raw.Length == 0) { return null; }
                DateTime v;
                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out v)) { return v; }
            }
            catch
            {
            }
            return null;
        }

        private void LoadState()
        {
            try
            {
                if (!File.Exists(_statePath)) { return; }
                string text = File.ReadAllText(_statePath, Encoding.UTF8);
                _lowSince = ReadIsoTime(text, "LowLoadSince");
                _highSince = ReadIsoTime(text, "HighLoadSince");
                _lastLoadState = ReadString(text, "LastLoadState");
                _currentKey = ReadString(text, "CurrentMode");
            }
            catch (Exception ex)
            {
                Logger.Warn("读取状态文件失败，忽略: " + Logger.Describe(ex));
            }
        }

        private void SaveState()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("    \"LowLoadSince\":  " + IsoOrNull(_lowSince) + ",");
                sb.AppendLine("    \"HighLoadSince\":  " + IsoOrNull(_highSince) + ",");
                sb.AppendLine("    \"LastLoadState\":  " + (string.IsNullOrEmpty(_lastLoadState) ? "null" : "\"" + _lastLoadState + "\"") + ",");
                sb.AppendLine("    \"CurrentMode\":  " + (string.IsNullOrEmpty(_currentKey) ? "null" : "\"" + _currentKey + "\"") + ",");
                sb.AppendLine("    \"LastCheckTime\":  \"" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "\"");
                sb.AppendLine("}");
                File.WriteAllText(_statePath, sb.ToString(), new UTF8Encoding(true));
            }
            catch
            {
                // 状态文件写失败不影响切换本身。
            }
        }

        private static string IsoOrNull(DateTime? v)
        {
            if (!v.HasValue) { return "null"; }
            return "\"" + v.Value.ToString("yyyy-MM-ddTHH:mm:ss") + "\"";
        }

        private static DateTime? ReadIsoTime(string text, string key)
        {
            string raw = ReadString(text, key);
            if (string.IsNullOrEmpty(raw) || raw == "null") { return null; }
            DateTime v;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out v)) { return v; }
            return null;
        }

        private static string ReadString(string text, string key)
        {
            int idx = text.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) { return null; }
            int colon = text.IndexOf(':', idx);
            if (colon < 0) { return null; }
            int p = colon + 1;
            while (p < text.Length && char.IsWhiteSpace(text[p])) { p++; }
            if (p >= text.Length) { return null; }
            if (text[p] == '"')
            {
                int end = text.IndexOf('"', p + 1);
                if (end < 0) { return null; }
                return text.Substring(p + 1, end - p - 1);
            }
            int stop = p;
            while (stop < text.Length && text[stop] != ',' && text[stop] != '}' && text[stop] != '\r' && text[stop] != '\n') { stop++; }
            return text.Substring(p, stop - p).Trim();
        }

        private void RaiseChanged()
        {
            EventHandler h = Changed;
            if (h != null)
            {
                try { h(this, EventArgs.Empty); }
                catch (Exception ex) { Logger.Warn("Changed 事件处理异常: " + Logger.Describe(ex)); }
            }
        }
    }
}
