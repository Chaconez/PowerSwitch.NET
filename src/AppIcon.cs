using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace PowerSwitch
{
    /// <summary>
    /// 图标统一入口。托盘、设置窗口、任务栏都用这里的同一份实例，
    /// 保证「左上角图标」和「托盘图标」永远是同一个样子。
    ///
    /// 图标是嵌入资源（PowerSwitch.icons.icon-*.ico），按名字缓存：
    /// 每次 new Icon 会各自持有一份 GDI 句柄，而托盘每几秒就要刷一次图标，
    /// 不缓存的话句柄会一路涨上去。
    /// </summary>
    internal static class AppIcon
    {
        public const string Saver = "saver";
        public const string Balanced = "balanced";
        public const string High = "high";
        public const string Paused = "paused";
        public const string Unknown = "unknown";

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Icon> Cache =
            new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);

        public static Icon Load(string name)
        {
            if (string.IsNullOrEmpty(name)) { name = Unknown; }
            lock (Sync)
            {
                Icon cached;
                if (Cache.TryGetValue(name, out cached)) { return cached; }
                Icon icon = LoadCore(name);
                Cache[name] = icon;
                return icon;
            }
        }

        private static Icon LoadCore(string name)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("PowerSwitch.icons.icon-" + name + ".ico"))
                {
                    if (s != null) { return new Icon(s); }
                }
                Logger.Warn("找不到嵌入图标: " + name + "，回落到系统图标");
            }
            catch (Exception ex)
            {
                Logger.Warn("载入图标失败 " + name + ": " + Logger.Describe(ex));
            }
            return SystemIcons.Application;
        }

        /// <summary>按当前状态挑图标：暂停是空心，其余按生效方案着色。</summary>
        public static string NameFor(PowerEngine engine)
        {
            if (engine == null) { return Unknown; }
            if (engine.IsPaused && !engine.IsRemoteOverrideActive) { return Paused; }

            string key = engine.CurrentKey;
            if (key == PowerScheme.Saver) { return Saver; }
            if (key == PowerScheme.HighPerformance || key == PowerScheme.UltimatePerformance) { return High; }
            if (key == PowerScheme.Balanced) { return Balanced; }
            return Unknown;
        }

        public static Icon ForEngine(PowerEngine engine)
        {
            return Load(NameFor(engine));
        }
    }
}
