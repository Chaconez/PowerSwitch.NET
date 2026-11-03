using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PowerSwitch
{
    /// <summary>
    /// 配置模型。字段名与旧 PowerShell 版 power_config.json 完全一致，
    /// 这样迁移时用户原有的配置文件可以直接沿用，不需要手工转换。
    /// </summary>
    internal sealed class AppConfig
    {
        public int NightStartHour = 23;
        public int NightEndHour = 7;
        public bool AllDayMonitor = true;
        public int CpuThreshold = 20;
        public int LowLoadDuration = 5;
        public int HighLoadDuration = 1;
        public int SampleCount = 2;
        public int SampleIntervalSec = 5;
        public int MaxLogSizeMB = 5;
        public int TrayRefreshSec = 15;
        public string DayMode = "Balanced";
        public string NightLowLoadMode = "PowerSaver";
        public string NightHighLoadMode = "Balanced";

        /// <summary>远程桌面连接时锁定方案并暂停自动切换。</summary>
        public bool PauseOnRemoteDesktop = true;
        /// <summary>远程桌面连接期间锁定到哪个方案。</summary>
        public string RemoteDesktopMode = "Balanced";
        /// <summary>远程桌面状态轮询间隔（秒）。事件通知之外的兜底频率。</summary>
        public int RdpPollSeconds = 3;

        public AppConfig Clone()
        {
            AppConfig c = new AppConfig();
            c.NightStartHour = NightStartHour;
            c.NightEndHour = NightEndHour;
            c.AllDayMonitor = AllDayMonitor;
            c.CpuThreshold = CpuThreshold;
            c.LowLoadDuration = LowLoadDuration;
            c.HighLoadDuration = HighLoadDuration;
            c.SampleCount = SampleCount;
            c.SampleIntervalSec = SampleIntervalSec;
            c.MaxLogSizeMB = MaxLogSizeMB;
            c.TrayRefreshSec = TrayRefreshSec;
            c.DayMode = DayMode;
            c.NightLowLoadMode = NightLowLoadMode;
            c.NightHighLoadMode = NightHighLoadMode;
            c.PauseOnRemoteDesktop = PauseOnRemoteDesktop;
            c.RemoteDesktopMode = RemoteDesktopMode;
            c.RdpPollSeconds = RdpPollSeconds;
            return c;
        }

        /// <summary>把越界或非法的值拉回合法区间，避免一份坏配置让程序行为诡异。</summary>
        public void Normalize()
        {
            NightStartHour = Clamp(NightStartHour, 0, 23, 23);
            NightEndHour = Clamp(NightEndHour, 0, 23, 7);
            CpuThreshold = Clamp(CpuThreshold, 1, 99, 20);
            LowLoadDuration = Clamp(LowLoadDuration, 1, 240, 5);
            HighLoadDuration = Clamp(HighLoadDuration, 1, 240, 1);
            SampleCount = Clamp(SampleCount, 1, 10, 2);
            SampleIntervalSec = Clamp(SampleIntervalSec, 1, 30, 5);
            MaxLogSizeMB = Clamp(MaxLogSizeMB, 1, 100, 5);
            TrayRefreshSec = Clamp(TrayRefreshSec, 5, 300, 15);
            DayMode = PowerScheme.NormalizeName(DayMode, "Balanced");
            NightLowLoadMode = PowerScheme.NormalizeName(NightLowLoadMode, "PowerSaver");
            NightHighLoadMode = PowerScheme.NormalizeName(NightHighLoadMode, "Balanced");
            RemoteDesktopMode = PowerScheme.NormalizeName(RemoteDesktopMode, "Balanced");
            RdpPollSeconds = Clamp(RdpPollSeconds, 1, 60, 3);
        }

        private static int Clamp(int v, int min, int max, int fallback)
        {
            if (v < min || v > max) { return fallback; }
            return v;
        }
    }

    /// <summary>
    /// 扁平 JSON 读写。配置只有一层键值，没必要引入序列化框架；
    /// 手写解析还能保证输出格式和旧版一致（4 空格缩进、键序固定）。
    /// </summary>
    internal static class ConfigStore
    {
        public static AppConfig Load(string path)
        {
            AppConfig cfg = new AppConfig();
            try
            {
                if (!File.Exists(path)) { return cfg; }
                string text = File.ReadAllText(path, Encoding.UTF8);
                Dictionary<string, string> map = ParseFlat(text);

                cfg.NightStartHour = GetInt(map, "NightStartHour", cfg.NightStartHour);
                cfg.NightEndHour = GetInt(map, "NightEndHour", cfg.NightEndHour);
                cfg.AllDayMonitor = GetBool(map, "AllDayMonitor", cfg.AllDayMonitor);
                cfg.CpuThreshold = GetInt(map, "CpuThreshold", cfg.CpuThreshold);
                cfg.LowLoadDuration = GetInt(map, "LowLoadDuration", cfg.LowLoadDuration);
                cfg.HighLoadDuration = GetInt(map, "HighLoadDuration", cfg.HighLoadDuration);
                cfg.SampleCount = GetInt(map, "SampleCount", cfg.SampleCount);
                cfg.SampleIntervalSec = GetInt(map, "SampleIntervalSec", cfg.SampleIntervalSec);
                cfg.MaxLogSizeMB = GetInt(map, "MaxLogSizeMB", cfg.MaxLogSizeMB);
                cfg.TrayRefreshSec = GetInt(map, "TrayRefreshSec", cfg.TrayRefreshSec);
                cfg.DayMode = GetString(map, "DayMode", cfg.DayMode);
                cfg.NightLowLoadMode = GetString(map, "NightLowLoadMode", cfg.NightLowLoadMode);
                cfg.NightHighLoadMode = GetString(map, "NightHighLoadMode", cfg.NightHighLoadMode);
                cfg.PauseOnRemoteDesktop = GetBool(map, "PauseOnRemoteDesktop", cfg.PauseOnRemoteDesktop);
                cfg.RemoteDesktopMode = GetString(map, "RemoteDesktopMode", cfg.RemoteDesktopMode);
                cfg.RdpPollSeconds = GetInt(map, "RdpPollSeconds", cfg.RdpPollSeconds);
            }
            catch (Exception ex)
            {
                Logger.Error("读取配置失败，使用默认值: " + path, ex);
            }
            cfg.Normalize();
            return cfg;
        }

        public static bool Save(string path, AppConfig cfg)
        {
            try
            {
                cfg.Normalize();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                AppendInt(sb, "NightStartHour", cfg.NightStartHour, true);
                AppendInt(sb, "NightEndHour", cfg.NightEndHour, true);
                AppendRaw(sb, "AllDayMonitor", cfg.AllDayMonitor ? "true" : "false", true);
                AppendInt(sb, "CpuThreshold", cfg.CpuThreshold, true);
                AppendInt(sb, "LowLoadDuration", cfg.LowLoadDuration, true);
                AppendInt(sb, "HighLoadDuration", cfg.HighLoadDuration, true);
                AppendInt(sb, "SampleCount", cfg.SampleCount, true);
                AppendInt(sb, "SampleIntervalSec", cfg.SampleIntervalSec, true);
                AppendInt(sb, "MaxLogSizeMB", cfg.MaxLogSizeMB, true);
                AppendInt(sb, "TrayRefreshSec", cfg.TrayRefreshSec, true);
                AppendRaw(sb, "DayMode", Quote(cfg.DayMode), true);
                AppendRaw(sb, "NightLowLoadMode", Quote(cfg.NightLowLoadMode), true);
                AppendRaw(sb, "NightHighLoadMode", Quote(cfg.NightHighLoadMode), true);
                AppendRaw(sb, "PauseOnRemoteDesktop", cfg.PauseOnRemoteDesktop ? "true" : "false", true);
                AppendRaw(sb, "RemoteDesktopMode", Quote(cfg.RemoteDesktopMode), true);
                AppendInt(sb, "RdpPollSeconds", cfg.RdpPollSeconds, false);
                sb.AppendLine("}");

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("写入配置失败: " + path, ex);
                return false;
            }
        }

        private static void AppendInt(StringBuilder sb, string key, int value, bool comma)
        {
            AppendRaw(sb, key, value.ToString(CultureInfo.InvariantCulture), comma);
        }

        private static void AppendRaw(StringBuilder sb, string key, string rawValue, bool comma)
        {
            sb.Append("    \"").Append(key).Append("\":  ").Append(rawValue);
            if (comma) { sb.Append(','); }
            sb.AppendLine();
        }

        private static string Quote(string s)
        {
            if (s == null) { return "\"\""; }
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>只解析最外层 "Key": value 的扁平结构，够用且不依赖第三方库。</summary>
        private static Dictionary<string, string> ParseFlat(string json)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(json)) { return map; }

            int i = 0;
            int n = json.Length;
            while (i < n)
            {
                int keyStart = json.IndexOf('"', i);
                if (keyStart < 0) { break; }
                int keyEnd = json.IndexOf('"', keyStart + 1);
                if (keyEnd < 0) { break; }
                string key = json.Substring(keyStart + 1, keyEnd - keyStart - 1);

                int colon = json.IndexOf(':', keyEnd + 1);
                if (colon < 0) { break; }

                int p = colon + 1;
                while (p < n && char.IsWhiteSpace(json[p])) { p++; }
                if (p >= n) { break; }

                string value;
                if (json[p] == '"')
                {
                    int end = p + 1;
                    StringBuilder vb = new StringBuilder();
                    while (end < n)
                    {
                        char ch = json[end];
                        if (ch == '\\' && end + 1 < n)
                        {
                            char esc = json[end + 1];
                            if (esc == 'n') { vb.Append('\n'); }
                            else if (esc == 't') { vb.Append('\t'); }
                            else { vb.Append(esc); }
                            end += 2;
                            continue;
                        }
                        if (ch == '"') { break; }
                        vb.Append(ch);
                        end++;
                    }
                    value = vb.ToString();
                    i = end + 1;
                }
                else
                {
                    int end = p;
                    while (end < n && json[end] != ',' && json[end] != '}' && json[end] != '\r' && json[end] != '\n') { end++; }
                    value = json.Substring(p, end - p).Trim();
                    i = end;
                }

                if (!map.ContainsKey(key)) { map[key] = value; }
            }
            return map;
        }

        private static int GetInt(Dictionary<string, string> map, string key, int fallback)
        {
            string raw;
            if (!map.TryGetValue(key, out raw)) { return fallback; }
            int v;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) { return v; }
            return fallback;
        }

        private static bool GetBool(Dictionary<string, string> map, string key, bool fallback)
        {
            string raw;
            if (!map.TryGetValue(key, out raw)) { return fallback; }
            if (raw == null) { return fallback; }
            string s = raw.Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes" || s == "on") { return true; }
            if (s == "false" || s == "0" || s == "no" || s == "off") { return false; }
            return fallback;
        }

        private static string GetString(Dictionary<string, string> map, string key, string fallback)
        {
            string raw;
            if (!map.TryGetValue(key, out raw)) { return fallback; }
            if (string.IsNullOrEmpty(raw)) { return fallback; }
            return raw.Trim();
        }
    }
}
