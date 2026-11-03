using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PowerSwitch
{
    internal static class Program
    {
        /// <summary>
        /// 与旧 PowerShell 托盘使用同一个互斥量名，这样升级期间不会出现两个图标。
        /// </summary>
        private const string MutexName = "Global\\PowerSwitchTray";

        [STAThread]
        private static void Main(string[] args)
        {
            // 必须在创建任何窗口句柄之前调用。SystemAware 等价于旧清单里的
            // dpiAware=true + dpiAwareness=system：程序自己按真实 DPI 缩放，
            // 否则 200% 屏幕上 Windows 会把整窗按 96dpi 渲染再位图放大，文字发虚。
            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
            }
            catch (Exception ex)
            {
                Logger.Init(Paths.LogFile, 5);
                Logger.Warn("设置 DPI 模式失败（继续启动）: " + Logger.Describe(ex));
            }

            bool createdNew = false;
            Mutex mutex = null;
            try
            {
                mutex = new Mutex(true, MutexName, out createdNew);
            }
            catch (Exception ex)
            {
                Logger.Init(Paths.LogFile, 5);
                Logger.Error("创建互斥量失败（继续启动）", ex);
                createdNew = true;
            }

            if (!createdNew)
            {
                // 已有实例在跑：直接退出，避免出现两个托盘图标
                return;
            }

            AppConfig cfg = ConfigStore.Load(Paths.ConfigFile);
            Logger.Init(Paths.LogFile, cfg.MaxLogSizeMB);

            // 启动即清掉「用户主动退出」标记：否则上次点过退出，这次手动启动会被看门狗忽略
            try
            {
                string stopped = Path.Combine(Paths.DataDir, "tray.stopped");
                if (File.Exists(stopped)) { File.Delete(stopped); }
            }
            catch
            {
            }

            // 单进程架构的核心保障：任何未捕获异常都只记日志，不杀进程。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainException;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 显式安装 WinForms 同步上下文：TrayApp 构造时就要拿它做 UI 线程调度，
            // 而 Application.Run 之前默认上下文是 null 或不匹配的，会导致 Post 丢消息。
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            try
            {
                string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                Logger.Info("========== PowerSwitch " + version + " (" + RuntimeInformation.FrameworkDescription + ") ==========");
                Logger.Info("工作目录: " + Paths.DataDir);
                Logger.Info("会话 ID: " + Process.GetCurrentProcess().SessionId);

                if (Process.GetCurrentProcess().SessionId == 0)
                {
                    Logger.Warn("当前运行在 session 0，托盘图标不可见。请改为在用户会话中启动。");
                }

                using (TrayApp app = new TrayApp())
                {
                    Application.Run(app);
                }
                Logger.Info("托盘进程正常退出");
            }
            catch (Exception ex)
            {
                Logger.Error("主循环异常退出", ex);
                try
                {
                    MessageBox.Show(
                        "PowerSwitch 启动失败：\n" + ex.Message + "\n\n详情见日志：\n" + Paths.LogFile,
                        "PowerSwitch",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch
                {
                }
            }
            finally
            {
                try
                {
                    if (mutex != null) { mutex.ReleaseMutex(); }
                    if (mutex != null) { mutex.Dispose(); }
                }
                catch
                {
                }
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Logger.Error("UI 线程未处理异常", e.Exception);
        }

        private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            if (ex != null) { Logger.Error("未处理异常（IsTerminating=" + e.IsTerminating + "）", ex); }
            else { Logger.Error("未处理异常（非 Exception 对象）"); }
        }
    }
}
