using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace PowerSwitch
{
    /// <summary>
    /// 托盘常驻主体。与旧 PowerShell 版最大的结构差异：
    ///   旧版 = 计划任务每分钟起一个新进程 + 另一个进程画图标
    ///   新版 = 单个进程，内部定时器驱动引擎，图标和引擎同生共死
    ///
    /// 图标丢失的防护做了三层：
    ///   1. 响应 explorer 广播的 TaskbarCreated 消息，重建任务栏后重新注册图标
    ///   2. 周期性比对 explorer 主进程 PID，发现变化就主动重注册
    ///   3. 任何 UI 回调都包在 try/catch 里，异常不会把进程带走
    /// </summary>
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly PowerEngine _engine;
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly System.Threading.Timer _tickTimer;
        private readonly System.Threading.Timer _reqTimer;
        private readonly System.Windows.Forms.Timer _uiTimer;
        private readonly SynchronizationContext _uiContext;
        private readonly TaskbarWatcher _watcher;
        private readonly RdpMonitor _rdp;

        private readonly ToolStripMenuItem _miStatus;
        private readonly ToolStripMenuItem _miRdp;
        private readonly ToolStripMenuItem _miPause;
        private readonly ToolStripMenuItem _miResume;
        private readonly ToolStripMenuItem _miPauseUntil;

        private Icon _currentIcon;
        private int _lastShellPid;
        private bool _disposed;
        private bool _ticking;
        private SettingsForm _settingsForm;

        public TrayApp()
        {
            _uiContext = SynchronizationContext.Current;
            _engine = new PowerEngine(Paths.DataDir);
            _engine.Changed += OnEngineChanged;

            // 全机电源方案只能有一个实例说了算。抢到就是驱动者，
            // 抢不到就进只读模式：照样显示状态，但不改方案、不写状态文件。
            EngineLeader.TryAcquire();
            _engine.IsDriver = EngineLeader.IsLeader;
            if (!_engine.IsDriver)
            {
                int lsid = EngineLeader.LeaderSession;
                Logger.Info(lsid > 0
                    ? "本会话为只读实例，电源方案由会话 " + lsid + " 驱动"
                    : "本会话为只读实例，电源方案由其他会话驱动");
            }

            _menu = BuildMenu(out _miStatus, out _miRdp, out _miPause, out _miResume, out _miPauseUntil);
            _menu.Opening += OnMenuOpening;

            _icon = new NotifyIcon();
            _icon.ContextMenuStrip = _menu;
            _icon.Text = "PowerSwitch";
            _icon.DoubleClick += delegate { OpenSettings(); };
            _icon.Visible = true;

            ApplyIcon();
            UpdateTooltip();

            // 响应任务栏重建（explorer 崩溃/重启后图标会集体消失，这是最常见的"图标丢失"）
            int taskbarMsg = PowerNative.TaskbarCreatedMessage();
            _watcher = new TaskbarWatcher(taskbarMsg, delegate { RestoreIcon("收到 TaskbarCreated"); });

            // 远程桌面监控：外部机器连进来就锁定方案，断开后恢复自动切换。
            // 独立于 60 秒的引擎周期，否则"立即切换"根本谈不上。
            _rdp = new RdpMonitor(_engine.Config.RdpPollSeconds);
            _rdp.Changed += OnRdpChanged;

            // 后台线程跑引擎：采样会阻塞数秒，绝不能在 UI 线程上做
            _tickTimer = new System.Threading.Timer(OnTickTimer, null, 3000, 60000);

            // 只读会话提交的切换请求由驱动者消费。2 秒一轮，
            // 保证在非驱动会话里点「节能/平衡」也有及时反馈，不用等 60 秒周期。
            _reqTimer = new System.Threading.Timer(OnReqTimer, null, 2000, 2000);

            // UI 侧轻量刷新：图标与提示文字
            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = Math.Max(1, _engine.Config.TrayRefreshSec) * 1000;
            _uiTimer.Tick += delegate { SafeUi(RefreshUi); };
            _uiTimer.Start();

            _lastShellPid = CurrentShellPid();
            Logger.Info("PowerSwitch 启动 (pid " + Process.GetCurrentProcess().Id + ", 会话 " + Process.GetCurrentProcess().SessionId + ")");
            Logger.Info("配置: " + _engine.ConfigPath);

            // --settings：启动后直接弹出设置窗口（装完就能改配置，不用先找托盘）
            string[] argv = Environment.GetCommandLineArgs();
            foreach (string a in argv)
            {
                if (string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase))
                {
                    System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                    t.Interval = 600;
                    t.Tick += delegate
                    {
                        t.Stop();
                        t.Dispose();
                        OpenSettings();
                    };
                    t.Start();
                    break;
                }
            }
        }

        public PowerEngine Engine { get { return _engine; } }

        private ContextMenuStrip BuildMenu(out ToolStripMenuItem miStatus, out ToolStripMenuItem miRdp, out ToolStripMenuItem miPause, out ToolStripMenuItem miResume, out ToolStripMenuItem miPauseUntil)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;

            miStatus = new ToolStripMenuItem("正在读取状态…");
            miStatus.Enabled = false;
            menu.Items.Add(miStatus);

            miRdp = new ToolStripMenuItem("远程桌面已连接");
            miRdp.Enabled = false;
            miRdp.Visible = false;
            menu.Items.Add(miRdp);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miSettings = new ToolStripMenuItem("设置…");
            miSettings.Click += delegate { OpenSettings(); };
            menu.Items.Add(miSettings);

            ToolStripMenuItem miSwitch = new ToolStripMenuItem("立即切换");
            foreach (string key in PowerScheme.AllNames)
            {
                string captured = key;
                ToolStripMenuItem item = new ToolStripMenuItem(PowerScheme.DisplayName(key));
                item.Click += delegate { SafeUi(delegate { _engine.ForceSwitch(captured); }); };
                miSwitch.DropDownItems.Add(item);
            }
            menu.Items.Add(miSwitch);

            miPause = new ToolStripMenuItem("暂停自动切换");
            miPauseUntil = miPause;
            AddPauseItem(miPause, "30 分钟", TimeSpan.FromMinutes(30));
            AddPauseItem(miPause, "1 小时", TimeSpan.FromHours(1));
            AddPauseItem(miPause, "2 小时", TimeSpan.FromHours(2));
            AddPauseItem(miPause, "4 小时", TimeSpan.FromHours(4));
            miPause.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem miForever = new ToolStripMenuItem("直到手动恢复");
            miForever.Click += delegate { SafeUi(delegate { _engine.Pause(null); }); };
            miPause.DropDownItems.Add(miForever);
            menu.Items.Add(miPause);

            miResume = new ToolStripMenuItem("恢复自动切换");
            miResume.Click += delegate { SafeUi(delegate { _engine.Resume(); }); };
            miResume.Visible = false;
            menu.Items.Add(miResume);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miLog = new ToolStripMenuItem("打开日志");
            miLog.Click += delegate { OpenPath(Paths.LogFile); };
            menu.Items.Add(miLog);

            ToolStripMenuItem miConfig = new ToolStripMenuItem("打开配置目录");
            miConfig.Click += delegate { OpenPath(Paths.DataDir); };
            menu.Items.Add(miConfig);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miAbout = new ToolStripMenuItem("关于");
            miAbout.Click += delegate { ShowAbout(); };
            menu.Items.Add(miAbout);

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { ExitApp(); };
            menu.Items.Add(miExit);

            return menu;
        }

        private void AddPauseItem(ToolStripMenuItem parent, string text, TimeSpan span)
        {
            TimeSpan captured = span;
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += delegate { SafeUi(delegate { _engine.Pause(captured); }); };
            parent.DropDownItems.Add(item);
        }

        private void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                bool paused = _engine.IsPaused;
                bool rdp = _engine.IsRemoteOverrideActive;
                _miPause.Visible = !paused;
                _miResume.Visible = paused;
                _miRdp.Visible = rdp;
                if (rdp)
                {
                    _miRdp.Text = "远程桌面已连接 · 自动切换已暂停";
                }
                _miStatus.Text = BuildStatusText();
            }
            catch (Exception ex)
            {
                Logger.Warn("菜单刷新异常: " + Logger.Describe(ex));
            }
        }

        private string BuildStatusText()
        {
            string mode = PowerScheme.DisplayName(_engine.CurrentKey);
            string cpu = _engine.LastCpu >= 0 ? _engine.LastCpu.ToString("F0") + "%" : "--";
            string extra = "";
            if (_engine.IsRemoteOverrideActive) { extra = "　· 远程桌面"; }
            else if (_engine.IsPaused) { extra = "　· 已暂停"; }
            return "当前：" + mode + "　CPU " + cpu + extra;
        }

        private void OnTickTimer(object state)
        {
            if (_disposed) { return; }
            if (_ticking) { return; }   // 上一轮还没跑完（采样可能长达数十秒），跳过本轮
            _ticking = true;
            try
            {
                // 驱动者退出（会话注销 / 进程被杀）后互斥量会消失，
                // 只读实例在这里捡回来，不用重启托盘。
                if (!EngineLeader.IsLeader)
                {
                    if (EngineLeader.TryAcquire())
                    {
                        Logger.Info("接管电源方案驱动权（原驱动者已退出）");
                    }
                }

                _engine.IsDriver = EngineLeader.IsLeader;
                if (_engine.IsDriver) { EngineLeader.Heartbeat(); }

                _engine.Tick();
            }
            catch (Exception ex)
            {
                Logger.Error("定时器回调异常", ex);
            }
            finally
            {
                _ticking = false;
            }
        }

        /// <summary>驱动者侧的请求泵：把只读会话提交的切换意图执行掉。</summary>
        private void OnReqTimer(object state)
        {
            if (_disposed) { return; }
            try
            {
                // 顺便在这里维持选举状态：驱动者退出后 2 秒内就能被别人接手，
                // 不用等 60 秒的引擎周期。
                if (!EngineLeader.IsLeader) { EngineLeader.TryAcquire(); }
                _engine.IsDriver = EngineLeader.IsLeader;

                if (_engine.IsDriver)
                {
                    EngineLeader.Heartbeat();
                    _engine.PumpRequests();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("处理切换请求异常: " + Logger.Describe(ex));
            }
        }

        private void OnEngineChanged(object sender, EventArgs e)
        {
            if (_uiContext != null)
            {
                _uiContext.Post(delegate { SafeUi(RefreshUi); }, null);
            }
        }

        private void RefreshUi()
        {
            ApplyIcon();
            UpdateTooltip();
            CheckShellRestart();
            CheckControlRequest();
            SyncTimers();
        }

        /// <summary>设置里改了刷新间隔/检测间隔就跟着调整，不用重启进程。</summary>
        private void SyncTimers()
        {
            try
            {
                int uiMs = Math.Max(1, _engine.Config.TrayRefreshSec) * 1000;
                if (_uiTimer.Interval != uiMs)
                {
                    _uiTimer.Interval = uiMs;
                    Logger.Info("托盘刷新间隔改为 " + (uiMs / 1000) + " 秒");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("同步托盘刷新间隔失败: " + Logger.Describe(ex));
            }

            try
            {
                int want = _engine.Config.RdpPollSeconds;
                if (_rdp != null && _rdp.PollSeconds != want) { _rdp.SetPollInterval(want); }
            }
            catch (Exception ex)
            {
                Logger.Warn("同步远程桌面检测间隔失败: " + Logger.Describe(ex));
            }
        }

        /// <summary>
        /// 远程桌面状态变化回调（后台线程）。连接要"立即"切换，所以直接在这里推给引擎，
        /// 不等 60 秒的引擎周期；断开后顺手踢一次 Tick，让负载逻辑马上重新接管。
        /// </summary>
        private void OnRdpChanged(object sender, EventArgs e)
        {
            try
            {
                RdpSessionInfo info = _rdp.Current;
                _engine.SetRemoteDesktopActive(info.Active, info.Describe());

                if (!info.Active)
                {
                    ThreadPool.QueueUserWorkItem(delegate { OnTickTimer(null); });
                }
            }
            catch (Exception ex)
            {
                Logger.Error("处理远程桌面状态变化失败", ex);
            }
        }

        /// <summary>
        /// 轻量控制通道：在运行目录放一个 open_settings.request 文件即可唤起设置窗口。
        /// 用于脚本、快捷方式或远程操作 —— 不必去找托盘图标，也不必模拟鼠标。
        /// </summary>
        private void CheckControlRequest()
        {
            try
            {
                string req = Path.Combine(Paths.DataDir, "open_settings.request");
                if (!File.Exists(req)) { return; }
                File.Delete(req);
                OpenSettings();
            }
            catch (Exception ex)
            {
                Logger.Warn("处理控制请求失败: " + Logger.Describe(ex));
            }
        }

        private void ApplyIcon()
        {
            // 图标选择逻辑收在 AppIcon 里，设置窗口用的是同一份，保证两边永远一致
            Icon target = AppIcon.ForEngine(_engine);
            if (!ReferenceEquals(_currentIcon, target))
            {
                _icon.Icon = target;
                _currentIcon = target;
            }
        }

        private void UpdateTooltip()
        {
            try
            {
                string mode = PowerScheme.DisplayName(_engine.CurrentKey);
                string state = "";
                if (_engine.IsRemoteOverrideActive) { state = " · 远程桌面"; }
                else if (_engine.IsPaused) { state = "（已暂停）"; }
                string cpu = _engine.LastCpu >= 0 ? " CPU " + _engine.LastCpu.ToString("F0") + "%" : "";
                string text = "PowerSwitch · " + mode + state + cpu;
                if (text.Length > 63) { text = text.Substring(0, 63); }
                _icon.Text = text;
            }
            catch
            {
                // Text 超长或图标未就绪时会抛异常，忽略即可。
            }
        }

        /// <summary>
        /// 重新注册托盘图标。explorer 重建任务栏后，原来的图标句柄全部失效，
        /// 但 NotifyIcon.Visible 仍然是 true —— 所以只能靠"先关再开"强制重建。
        /// </summary>
        private void RestoreIcon(string reason)
        {
            try
            {
                _icon.Visible = false;
                _icon.Visible = true;
                _icon.Text = "PowerSwitch";
                UpdateTooltip();
                Logger.Info("重新注册托盘图标（" + reason + "）");
            }
            catch (Exception ex)
            {
                Logger.Error("重新注册托盘图标失败", ex);
            }
        }

        /// <summary>
        /// 双保险：TaskbarCreated 是广播消息，理论上够用；但若 explorer 是以
        /// 非标准方式重启，消息可能收不到，所以再盯一下 shell 进程 PID。
        /// </summary>
        private void CheckShellRestart()
        {
            try
            {
                int pid = CurrentShellPid();
                if (pid > 0 && _lastShellPid > 0 && pid != _lastShellPid)
                {
                    RestoreIcon("shell 进程变化 " + _lastShellPid + " -> " + pid);
                }
                if (pid > 0) { _lastShellPid = pid; }
            }
            catch
            {
            }
        }

        private static int CurrentShellPid()
        {
            try
            {
                int session = Process.GetCurrentProcess().SessionId;
                Process[] all = Process.GetProcessesByName("explorer");
                int fallback = 0;
                foreach (Process p in all)
                {
                    try
                    {
                        if (p.SessionId != session) { continue; }
                        // 桌面 shell 的映像是 Explorer.EXE，其它 /factory 实例是辅助进程
                        if (fallback == 0) { fallback = p.Id; }
                        string path = null;
                        try { path = p.MainModule.FileName; }
                        catch { }
                        if (path != null && path.EndsWith("Explorer.EXE", StringComparison.OrdinalIgnoreCase))
                        {
                            return p.Id;
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
                return fallback;
            }
            catch
            {
                return 0;
            }
        }

        private void OpenSettings()
        {
            SafeUi(delegate
            {
                // 单实例守卫：ShowDialog 跑的是嵌套消息循环，托盘菜单点击仍会被派发，
                // 连点几次就会叠出好几个设置窗口（旧版 PowerShell 踩过同样的坑）。
                if (_settingsForm != null && !_settingsForm.IsDisposed)
                {
                    try
                    {
                        if (_settingsForm.WindowState == FormWindowState.Minimized)
                        {
                            _settingsForm.WindowState = FormWindowState.Normal;
                        }
                        _settingsForm.Activate();
                        _settingsForm.BringToFront();
                    }
                    catch { }
                    return;
                }

                try
                {
                    _settingsForm = new SettingsForm(_engine);
                    Logger.Info("设置窗口已打开");
                    _settingsForm.ShowDialog();
                    Logger.Info("设置窗口已关闭");
                }
                catch (Exception ex)
                {
                    Logger.Error("打开设置窗口失败", ex);
                    MessageBox.Show("打开设置窗口失败：" + ex.Message, "PowerSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    try { if (_settingsForm != null) { _settingsForm.Dispose(); } } catch { }
                    _settingsForm = null;
                    ApplyIcon();
                    UpdateTooltip();
                }
            });
        }

        private void ShowAbout()
        {
            SafeUi(delegate
            {
                string version = "1.0.0.0";
                try { version = Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
                catch { }
                string text =
                    "PowerSwitch " + version + Environment.NewLine +
                    "电源计划自动切换" + Environment.NewLine + Environment.NewLine +
                    "按 CPU 负载与夜间时段，自动在节能 / 平衡 / 高性能之间切换，" +
                    "降低 NAS 空载待机功耗。" + Environment.NewLine +
                    "检测到远程桌面连接时锁定方案并暂停自动切换，断开后自动恢复。" + Environment.NewLine + Environment.NewLine +
                    "运行目录：" + Paths.DataDir + Environment.NewLine +
                    "当前会话：" + Process.GetCurrentProcess().SessionId;
                MessageBox.Show(text, "关于 PowerSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        private void OpenPath(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    MessageBox.Show("路径不存在：" + path, "PowerSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex)
            {
                Logger.Warn("打开路径失败: " + path + " :: " + Logger.Describe(ex));
            }
        }

        private void ExitApp()
        {
            try
            {
                Logger.Info("用户从托盘退出");
                // 留个标记：看门狗看到它就不会立刻把进程拉回来（宽限期 30 分钟）。
                // 没有这个文件的话，「退出」15 秒后图标就又出现了。
                try
                {
                    File.WriteAllText(Path.Combine(Paths.DataDir, "tray.stopped"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                }
                catch (Exception ex)
                {
                    Logger.Warn("写入退出标记失败: " + Logger.Describe(ex));
                }
                _tickTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _uiTimer.Stop();
                _icon.Visible = false;
                _icon.Dispose();
            }
            catch
            {
            }
            ExitThread();
        }

        /// <summary>把动作丢回 UI 线程执行，并且保证异常不外溢。</summary>
        private void SafeUi(Action action)
        {
            try
            {
                if (_uiContext != null && SynchronizationContext.Current != _uiContext)
                {
                    _uiContext.Post(delegate { SafeUi(action); }, null);
                    return;
                }
                action();
            }
            catch (Exception ex)
            {
                Logger.Error("UI 回调异常", ex);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                try { if (_rdp != null) { _rdp.Dispose(); } } catch { }
                try { if (_tickTimer != null) { _tickTimer.Dispose(); } } catch { }
                try { if (_reqTimer != null) { _reqTimer.Dispose(); } } catch { }
                try { if (_uiTimer != null) { _uiTimer.Dispose(); } } catch { }
                try { if (_watcher != null) { _watcher.ReleaseHandle(); } } catch { }
                try { if (_icon != null) { _icon.Visible = false; _icon.Dispose(); } } catch { }
                try { if (_menu != null) { _menu.Dispose(); } } catch { }
                // 让出驱动权：别的会话的只读实例下一轮就能接手，不必等进程彻底消失
                try { EngineLeader.Release(); } catch { }
            }
            base.Dispose(disposing);
        }

        /// <summary>接收 explorer 广播消息的隐藏窗口。</summary>
        private sealed class TaskbarWatcher : NativeWindow
        {
            private readonly int _message;
            private readonly Action _onTaskbarCreated;

            public TaskbarWatcher(int message, Action onTaskbarCreated)
            {
                _message = message;
                _onTaskbarCreated = onTaskbarCreated;
                try
                {
                    CreateParams cp = new CreateParams();
                    cp.Caption = "PowerSwitchTaskbarWatcher";
                    cp.Style = 0;
                    cp.ExStyle = 0;
                    CreateHandle(cp);
                }
                catch (Exception ex)
                {
                    Logger.Warn("创建 TaskbarCreated 监听窗口失败: " + Logger.Describe(ex));
                }
            }

            protected override void WndProc(ref Message m)
            {
                if (_message != 0 && m.Msg == _message)
                {
                    try { _onTaskbarCreated(); }
                    catch (Exception ex) { Logger.Error("处理 TaskbarCreated 失败", ex); }
                }
                base.WndProc(ref m);
            }
        }
    }
}
