using System;
using System.IO;
using System.Text;

namespace PowerSwitch
{
    /// <summary>
    /// 极简文件日志。所有写操作加锁并吞掉异常 —— 日志写失败绝不能影响主流程。
    /// 超过 MaxLogSizeMB 时轮转成 .1（只保留一份归档，和旧 PowerShell 版行为一致）。
    /// </summary>
    internal static class Logger
    {
        private static readonly object Sync = new object();
        private static string _logPath;
        private static long _maxBytes = 5L * 1024L * 1024L;
        private static bool _fellBack;

        public static void Init(string logPath, int maxSizeMb)
        {
            _logPath = logPath;
            if (maxSizeMb < 1) { maxSizeMb = 1; }
            _maxBytes = (long)maxSizeMb * 1024L * 1024L;
            _fellBack = false;
        }

        /// <summary>
        /// 主日志写不进去时，退到「按会话区分」的日志文件。
        ///
        /// 触发场景：多用户共用同一个 ProgramData 目录时，日志文件的属主是
        /// 先创建它的那个账户（常见是 SYSTEM 或管理员），其他账户只有读权限。
        /// 不降级的话，非属主会话的实例会彻底哑掉 —— 驱动者没有日志，等于无法排查。
        /// </summary>
        private static void FallbackToSessionLog()
        {
            if (_fellBack || string.IsNullOrEmpty(_logPath)) { return; }
            _fellBack = true;
            try
            {
                int sid = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                string dir = Path.GetDirectoryName(_logPath);
                string name = Path.GetFileNameWithoutExtension(_logPath);
                string ext = Path.GetExtension(_logPath);
                _logPath = Path.Combine(dir, name + ".session" + sid + ext);
                // 直接写一行，让降级本身留下痕迹
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [WARN] 主日志不可写，已切换到 " +
                              Path.GetFileName(_logPath) + Environment.NewLine;
                File.AppendAllText(_logPath, line, Encoding.UTF8);
            }
            catch
            {
            }
        }

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }
        public static void Error(string message) { Write("ERROR", message); }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message + " :: " + Describe(ex));
        }

        /// <summary>把异常摊平成单行，保留内部异常链 —— 排查时最有用的是最内层。</summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) { return "(null)"; }
            StringBuilder sb = new StringBuilder();
            Exception cur = ex;
            int depth = 0;
            while (cur != null && depth < 5)
            {
                if (depth > 0) { sb.Append(" <- "); }
                sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
                cur = cur.InnerException;
                depth++;
            }
            if (ex.StackTrace != null)
            {
                string[] lines = ex.StackTrace.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0) { sb.Append(" @ ").Append(lines[0].Trim()); }
            }
            return sb.ToString();
        }

        private static void Write(string level, string message)
        {
            if (string.IsNullOrEmpty(_logPath)) { return; }
            lock (Sync)
            {
                try
                {
                    Rotate();
                    string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + level + "] " + message + Environment.NewLine;
                    File.AppendAllText(_logPath, line, Encoding.UTF8);
                }
                catch
                {
                    // 主日志写不进去（多半是别的账户创建的、当前账户只有读权限）：
                    // 降级到按会话命名的日志，再试一次。仍然失败就静默 —— 主功能优先。
                    if (!_fellBack)
                    {
                        FallbackToSessionLog();
                        try
                        {
                            string line2 = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + level + "] " + message + Environment.NewLine;
                            File.AppendAllText(_logPath, line2, Encoding.UTF8);
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private static void Rotate()
        {
            try
            {
                FileInfo fi = new FileInfo(_logPath);
                if (!fi.Exists || fi.Length < _maxBytes) { return; }
                string archived = _logPath + ".1";
                if (File.Exists(archived)) { File.Delete(archived); }
                File.Move(_logPath, archived);
            }
            catch
            {
                // 轮转失败不阻塞写入。
            }
        }
    }
}
