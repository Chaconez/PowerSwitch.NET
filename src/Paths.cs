using System;
using System.IO;

namespace PowerSwitch
{
    /// <summary>运行期路径。全部相对 exe 所在目录，部署到哪就在哪工作。</summary>
    internal static class Paths
    {
        public static string DataDir
        {
            get
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                if (string.IsNullOrEmpty(dir)) { dir = Directory.GetCurrentDirectory(); }
                return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        public static string LogFile { get { return Path.Combine(DataDir, "power_switch.log"); } }
        public static string TrayLogFile { get { return Path.Combine(DataDir, "tray.log"); } }
        public static string ConfigFile { get { return Path.Combine(DataDir, "power_config.json"); } }
        public static string ConfigDefault { get { return Path.Combine(DataDir, "power_config.default.json"); } }
        public static string StateFile { get { return Path.Combine(DataDir, "power_switch_state.json"); } }
        public static string PauseFile { get { return Path.Combine(DataDir, "automation.paused"); } }
        public static string WatchdogLog { get { return Path.Combine(DataDir, "watchdog.log"); } }
    }
}
