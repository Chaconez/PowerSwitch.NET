using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerSwitch
{
    /// <summary>
    /// 系统 API 封装。全部走 P/Invoke 直接调用，不再 spawn powercfg.exe ——
    /// 这是相对旧 PowerShell 版最主要的性能与可靠性改进：没有子进程，就没有
    /// "子进程被父进程 Job 连带杀死" 和 "退出码要看日志才知道" 的问题。
    /// </summary>
    internal static class PowerNative
    {
        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint RegisterWindowMessage(string lpString);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low;
            public uint High;

            public ulong ToUInt64()
            {
                return ((ulong)High << 32) | Low;
            }
        }

        public const uint ERROR_SUCCESS = 0;

        /// <summary>读取当前生效电源方案 GUID。失败返回 null。</summary>
        public static Guid? GetActiveScheme()
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                uint rc = PowerGetActiveScheme(IntPtr.Zero, out ptr);
                if (rc != ERROR_SUCCESS || ptr == IntPtr.Zero) { return null; }
                return (Guid)Marshal.PtrToStructure(ptr, typeof(Guid));
            }
            catch (Exception ex)
            {
                Logger.Error("PowerGetActiveScheme 调用失败", ex);
                return null;
            }
            finally
            {
                if (ptr != IntPtr.Zero) { LocalFree(ptr); }
            }
        }

        /// <summary>切换电源方案。返回 true 表示系统接受了这次切换。</summary>
        /// <summary>最近一次 SetActiveScheme 的返回码，0 表示成功。用于区分「没权限」和「偶发失败」。</summary>
        public static uint LastSchemeError { get; private set; }

        public static bool SetActiveScheme(Guid scheme)
        {
            try
            {
                Guid g = scheme;
                uint rc = PowerSetActiveScheme(IntPtr.Zero, ref g);
                LastSchemeError = rc;
                if (rc != ERROR_SUCCESS)
                {
                    Logger.Warn("PowerSetActiveScheme 返回 0x" + rc.ToString("X8") + "（" + DescribeLastError((int)rc) + "）");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("PowerSetActiveScheme 调用失败", ex);
                return false;
            }
        }

        /// <summary>TaskbarCreated 是 explorer 重建任务栏时广播的消息号，运行时才确定。</summary>
        public static int TaskbarCreatedMessage()
        {
            try
            {
                return unchecked((int)RegisterWindowMessage("TaskbarCreated"));
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 采样 CPU 总占用率（0-100）。用 GetSystemTimes 而不是 PerformanceCounter：
        /// 后者首次取值恒为 0，且依赖性能计数器服务，在精简系统上可能不可用。
        /// </summary>
        public static double SampleCpuUsage(int intervalMs)
        {
            FileTime idle1, kernel1, user1;
            FileTime idle2, kernel2, user2;

            if (!GetSystemTimes(out idle1, out kernel1, out user1)) { return -1; }
            if (intervalMs > 0) { System.Threading.Thread.Sleep(intervalMs); }
            if (!GetSystemTimes(out idle2, out kernel2, out user2)) { return -1; }

            ulong idleDelta = idle2.ToUInt64() - idle1.ToUInt64();
            ulong kernelDelta = kernel2.ToUInt64() - kernel1.ToUInt64();
            ulong userDelta = user2.ToUInt64() - user1.ToUInt64();

            // kernel 时间包含 idle 时间，所以总时间是 kernel + user。
            ulong total = kernelDelta + userDelta;
            if (total == 0) { return 0; }

            double busy = (double)(total - idleDelta);
            double usage = busy * 100.0 / (double)total;
            if (usage < 0) { usage = 0; }
            if (usage > 100) { usage = 100; }
            return usage;
        }

        /// <summary>进程是否在活动用户会话里（不是 session 0）。</summary>
        public static bool IsInteractiveSession()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().SessionId != 0;
            }
            catch
            {
                return true;
            }
        }

        public static string DescribeLastError(int code)
        {
            try
            {
                return new System.ComponentModel.Win32Exception(code).Message;
            }
            catch
            {
                return "code=" + code;
            }
        }
    }
}
