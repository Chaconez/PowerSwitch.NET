using System;
using System.Collections.Generic;

namespace PowerSwitch
{
    /// <summary>
    /// Windows 内置电源方案。GUID 是系统固定值，不随语言变化。
    /// </summary>
    internal sealed class PowerScheme
    {
        public const string Saver = "PowerSaver";
        public const string Balanced = "Balanced";
        public const string HighPerformance = "HighPerformance";
        public const string UltimatePerformance = "UltimatePerformance";

        private static readonly Dictionary<string, Guid> Ids = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            { Saver,               new Guid("a1841308-3541-4fab-bc81-f71556f20b4a") },
            { Balanced,            new Guid("381b4222-f694-41f0-9685-ff5bb260df2e") },
            { HighPerformance,     new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c") },
            { UltimatePerformance, new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61") }
        };

        private static readonly Dictionary<string, string> DisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { Saver,               "节能" },
            { Balanced,            "平衡" },
            { HighPerformance,     "高性能" },
            { UltimatePerformance, "卓越性能" }
        };

        public static IList<string> AllNames
        {
            get { return new List<string>(new string[] { Saver, Balanced, HighPerformance, UltimatePerformance }); }
        }

        /// <summary>把配置里写的方案名规范成内部键名；无法识别时回落到 fallback。</summary>
        public static string NormalizeName(string raw, string fallback)
        {
            if (string.IsNullOrEmpty(raw)) { return fallback; }
            string s = raw.Trim();
            foreach (string key in Ids.Keys)
            {
                if (string.Equals(key, s, StringComparison.OrdinalIgnoreCase)) { return key; }
                if (string.Equals(DisplayNames[key], s, StringComparison.OrdinalIgnoreCase)) { return key; }
            }
            // 常见别名
            if (string.Equals(s, "Power Saver", StringComparison.OrdinalIgnoreCase)) { return Saver; }
            if (string.Equals(s, "High Performance", StringComparison.OrdinalIgnoreCase)) { return HighPerformance; }
            if (string.Equals(s, "Ultimate Performance", StringComparison.OrdinalIgnoreCase)) { return UltimatePerformance; }
            return fallback;
        }

        public static Guid ToGuid(string name)
        {
            Guid g;
            if (Ids.TryGetValue(NormalizeName(name, Balanced), out g)) { return g; }
            return Ids[Balanced];
        }

        public static string DisplayName(string name)
        {
            string key = NormalizeName(name, Balanced);
            return DisplayNames[key];
        }

        /// <summary>由 GUID 反查内部键名，用于识别当前生效方案；未知方案返回 null。</summary>
        public static string FromGuid(Guid id)
        {
            foreach (KeyValuePair<string, Guid> kv in Ids)
            {
                if (kv.Value == id) { return kv.Key; }
            }
            return null;
        }
    }
}
