using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PowerSwitchWatchdog
{
    /// <summary>
    /// 托盘进程看门狗。
    ///
    /// 背景：这台机器上「以交互式用户身份启动计划任务」被系统拒绝
    /// （0x800710E0 操作员或系统管理员拒绝了请求），所以不能靠计划任务
    /// 在用户会话里直接拉起 GUI 进程。但 SYSTEM 身份的计划任务是可用的，
    /// 而 SYSTEM 可以用 WTSQueryUserToken + CreateProcessAsUser 在指定会话里
    /// 创建进程 —— 这条路能绕开上面的限制。
    ///
    /// 职责只有一件：发现活动会话里没有 PowerSwitch.exe，就把它拉起来。
    /// 不参与任何电源逻辑。
    /// </summary>
    internal static class Program
    {
        private const string MutexName = "Global\\PowerSwitchWatchdog";
        private const string TargetExe = "PowerSwitch.exe";
        private const int CheckIntervalMs = 15000;
        private const int ManualExitGraceMinutes = 30;

        private static string _dataDir;
        private static string _logFile;

        private static int Main(string[] args)
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    Console.WriteLine("watchdog already running");
                    return 0;
                }

                _dataDir = ResolveDataDir(args);
                _logFile = Path.Combine(_dataDir, "watchdog.log");
                Log("watchdog started, dataDir=" + _dataDir + ", pid=" + Process.GetCurrentProcess().Id);

                bool once = Array.IndexOf(args, "--once") >= 0;
                if (once)
                {
                    CheckOnce();
                    Log("watchdog finished (--once)");
                    return 0;
                }

                while (true)
                {
                    try
                    {
                        CheckOnce();
                    }
                    catch (Exception ex)
                    {
                        Log("检查异常: " + ex.GetType().Name + ": " + ex.Message);
                    }
                    Thread.Sleep(CheckIntervalMs);
                }
            }
        }

        private static string ResolveDataDir(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--dir", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1].TrimEnd('\\');
                }
            }
            // 默认与 watchdog 同目录
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return baseDir.TrimEnd('\\');
        }

        private static void CheckOnce()
        {
            string exePath = Path.Combine(_dataDir, TargetExe);
            if (!File.Exists(exePath))
            {
                Log("目标程序不存在，跳过: " + exePath);
                return;
            }

            if (IsManualExitRecent())
            {
                return;
            }

            List<SessionInfo> sessions = EnumerateUserSessions();
            if (sessions.Count == 0)
            {
                return;
            }

            foreach (SessionInfo s in sessions)
            {
                if (HasProcessInSession(TargetExe, s.SessionId))
                {
                    continue;
                }
                Log(string.Format("会话 {0} ({1}) 内没有 {2}，尝试拉起", s.SessionId, s.StateName, TargetExe));
                if (LaunchInSession(exePath, _dataDir, s.SessionId))
                {
                    Log("已在该会话创建进程");
                }
                else
                {
                    Log("创建进程失败，详见上方错误码");
                }
            }
        }

        /// <summary>用户在托盘主动点「退出」后，短时间内不要再拉起，否则会显得阴魂不散。</summary>
        private static bool IsManualExitRecent()
        {
            try
            {
                string flag = Path.Combine(_dataDir, "tray.stopped");
                if (!File.Exists(flag)) { return false; }
                TimeSpan age = DateTime.Now - File.GetLastWriteTime(flag);
                if (age.TotalMinutes < ManualExitGraceMinutes) { return true; }
                File.Delete(flag);
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasProcessInSession(string processName, int sessionId)
        {
            try
            {
                string bare = Path.GetFileNameWithoutExtension(processName);
                Process[] list = Process.GetProcessesByName(bare);
                try
                {
                    foreach (Process p in list)
                    {
                        try
                        {
                            if (p.SessionId == sessionId) { return true; }
                        }
                        catch
                        {
                        }
                    }
                }
                finally
                {
                    foreach (Process p in list) { try { p.Dispose(); } catch { } }
                }
            }
            catch
            {
            }
            return false;
        }

        // ================= 会话枚举 =================

        private struct SessionInfo
        {
            public int SessionId;
            public string StateName;
            public bool CanLaunch;
        }

        private enum WTS_CONNECTSTATE_CLASS
        {
            WTSActive = 0,
            WTSConnected = 1,
            WTSConnectQuery = 2,
            WTSShadow = 3,
            WTSDisconnected = 4,
            WTSIdle = 5,
            WTSListen = 6,
            WTSReset = 7,
            WTSDown = 8,
            WTSInit = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_SESSION_INFO
        {
            public int SessionId;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pWinStationName;
            public WTS_CONNECTSTATE_CLASS State;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);

        private static List<SessionInfo> EnumerateUserSessions()
        {
            List<SessionInfo> result = new List<SessionInfo>();
            IntPtr buffer = IntPtr.Zero;
            int count = 0;
            try
            {
                if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out buffer, out count))
                {
                    Log("WTSEnumerateSessions 失败: " + Marshal.GetLastWin32Error());
                    return result;
                }

                int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(buffer.ToInt64() + (long)i * size);
                    WTS_SESSION_INFO info = (WTS_SESSION_INFO)Marshal.PtrToStructure(item, typeof(WTS_SESSION_INFO));

                    if (info.SessionId == 0) { continue; }   // session 0 是服务会话，不放 GUI

                    bool usable = info.State == WTS_CONNECTSTATE_CLASS.WTSActive
                               || info.State == WTS_CONNECTSTATE_CLASS.WTSDisconnected;

                    if (!usable) { continue; }

                    SessionInfo s = new SessionInfo();
                    s.SessionId = info.SessionId;
                    s.StateName = info.State.ToString();
                    s.CanLaunch = true;
                    result.Add(s);
                }
            }
            catch (Exception ex)
            {
                Log("枚举会话异常: " + ex.Message);
            }
            finally
            {
                if (buffer != IntPtr.Zero) { WTSFreeMemory(buffer); }
            }
            return result;
        }

        // ================= 跨会话创建进程 =================

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr existingToken,
            uint desiredAccess,
            IntPtr tokenAttributes,
            int impersonationLevel,
            int tokenType,
            out IntPtr newToken);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(
            IntPtr token,
            string applicationName,
            string commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref STARTUPINFO startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        private const uint TOKEN_ALL_ACCESS = 0xF01FF;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint CREATE_NO_WINDOW = 0x08000000;

        private static bool LaunchInSession(string exePath, string workDir, int sessionId)
        {
            IntPtr userToken = IntPtr.Zero;
            IntPtr primaryToken = IntPtr.Zero;
            IntPtr envBlock = IntPtr.Zero;

            try
            {
                if (!WTSQueryUserToken(sessionId, out userToken))
                {
                    Log("WTSQueryUserToken 失败 (session " + sessionId + "): " + Marshal.GetLastWin32Error());
                    return false;
                }

                if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
                {
                    Log("DuplicateTokenEx 失败: " + Marshal.GetLastWin32Error());
                    return false;
                }

                if (!CreateEnvironmentBlock(out envBlock, primaryToken, false))
                {
                    envBlock = IntPtr.Zero;   // 拿不到环境块也能启动，只是缺少用户环境变量
                }

                STARTUPINFO si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                si.lpDesktop = "winsta0\\default";

                PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
                string cmdLine = "\"" + exePath + "\"";
                bool ok = CreateProcessAsUser(
                    primaryToken,
                    exePath,
                    cmdLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                    envBlock,
                    workDir,
                    ref si,
                    out pi);

                if (!ok)
                {
                    Log("CreateProcessAsUser 失败: " + Marshal.GetLastWin32Error() + " (" + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message + ")");
                    return false;
                }

                Log("已启动 " + exePath + " 于会话 " + sessionId + "，pid=" + pi.dwProcessId);
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return true;
            }
            catch (Exception ex)
            {
                Log("跨会话启动异常: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
            finally
            {
                if (envBlock != IntPtr.Zero) { DestroyEnvironmentBlock(envBlock); }
                if (primaryToken != IntPtr.Zero) { CloseHandle(primaryToken); }
                if (userToken != IntPtr.Zero) { CloseHandle(userToken); }
            }
        }

        // ================= 日志 =================

        private static readonly object LogSync = new object();

        private static void Log(string message)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;
            try { Console.WriteLine(line); } catch { }
            try
            {
                lock (LogSync)
                {
                    if (_logFile != null)
                    {
                        File.AppendAllText(_logFile, line + Environment.NewLine, Encoding.UTF8);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
