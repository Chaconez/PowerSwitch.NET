using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace PowerSwitch
{
    /// <summary>
    /// 电源方案决策引擎。行为与旧 PowerShell 版 SmartPowerSwitch.ps1 完全对齐：
    ///
    ///   useLoadLogic = AllDayMonitor || 处于夜间时段
    ///   ├─ false（白天且未开全天监控）→ 固定 DayMode
    ///   └─ true → 按 CPU 负载走防抖状态机
    ///        ├─ 低负载持续 LowLoadDuration 分钟 → NightLowLoadMode
    ///        └─ 高负载持续 HighLoadDuration 分钟 → NightHighLoadMode
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

        public bool IsPaused
        {
            get { return File.Exists(_pausePath); }
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
            lock (_sync)
            {
                _cfg = ConfigStore.Load(_configPath);
                Logger.Info("配置已重新载入: " + _configPath);
            }
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
            if (File.Exists(_pausePath))
            {
                DateTime? until = ReadPauseUntil();
                if (until.HasValue && DateTime.Now >= until.Value)
                {
                    try { File.Delete(_pausePath); } catch { }
                    Logger.Info("暂停已到期，恢复自动切换");
                }
                else
                {
                    SetAction(until.HasValue ? "已暂停到 " + until.Value.ToString("HH:mm") : "已暂停（手动恢复）");
                    _lastTick = DateTime.Now;
                    return;
                }
            }

            RefreshCurrent();
            string current = _currentKey;

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
