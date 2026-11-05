using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace PowerSwitch
{
    /// <summary>
    /// 设置窗口。版式沿用 PowerShell 版的设计：分组卡片 + 顶部实时状态条 + 手动切换按钮。
    ///
    /// 两个和"看起来对不对"直接相关的点：
    ///
    /// 1. 绝对坐标，不用 TableLayoutPanel —— 上一版就是被表格布局把「高级」压到按钮底下去了。
    ///    分组数量是固定的，算好坐标反而更稳。
    ///
    /// 2. 自己按 DeviceDpi 缩放。程序在 app.manifest 里声明了 DPI 感知，Windows 不再帮忙
    ///    位图放大（那会把文字糊掉），所以 96dpi 的设计坐标必须自己乘缩放比。
    ///    字号不用管：9pt 在 192dpi 下本来就是 24px 高，系统会按真实 DPI 绘制。
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        // ---- 配色（与旧版一致）----
        private static readonly Color CCard = Color.FromArgb(255, 255, 255);
        private static readonly Color CBg = Color.FromArgb(245, 246, 248);
        private static readonly Color CMain = Color.FromArgb(33, 33, 33);
        private static readonly Color CSub = Color.FromArgb(117, 117, 117);
        private static readonly Color CGreen = Color.FromArgb(46, 125, 50);
        private static readonly Color CBlue = Color.FromArgb(21, 101, 192);
        private static readonly Color CRed = Color.FromArgb(198, 40, 40);
        private static readonly Color CLine = Color.FromArgb(198, 203, 209);
        private static readonly Color CLabel = Color.FromArgb(60, 64, 68);

        private readonly PowerEngine _engine;
        private readonly AppConfig _original;
        private readonly float _s;

        private readonly Font _fN;
        private readonly Font _fB;
        private readonly Font _fH;
        private readonly Font _fG;
        private readonly Font _fS;

        // 状态区
        private Label _lMode;
        private Label _lCpu;
        private Label _lMem;
        private Label _lAuto;
        private Label _lJudge;
        private Label _lSince;

        // 条件
        private NumericUpDown _nudCpu;
        private NumericUpDown _nudLow;
        private NumericUpDown _nudHigh;
        private NumericUpDown _nudNightStart;
        private NumericUpDown _nudNightEnd;
        private CheckBox _chkAllDay;

        // 映射
        private ComboBox _cmbDay;
        private ComboBox _cmbNightLow;
        private ComboBox _cmbNightHigh;

        // 远程桌面
        private CheckBox _chkRdp;
        private ComboBox _cmbRdpMode;
        private NumericUpDown _nudRdpPoll;

        // 高级
        private NumericUpDown _nudSample;
        private NumericUpDown _nudInterval;
        private NumericUpDown _nudLog;
        private NumericUpDown _nudRefresh;
        private CheckBox _chkAutoStart;
        private CheckBox _chkPause;

        private Label _hint;
        private Timer _statusTimer;

        public SettingsForm(PowerEngine engine)
        {
            _engine = engine;
            _original = engine.Config;
            _s = DetectScale();

            _fN = MakeFont(9F, FontStyle.Regular);
            _fB = MakeFont(9F, FontStyle.Bold);
            _fH = MakeFont(10.5F, FontStyle.Bold);
            _fG = MakeFont(8.5F, FontStyle.Bold);
            _fS = MakeFont(8F, FontStyle.Regular);

            BuildUi();
            CenterOnScreen();
            LoadValues(_original);
            UpdateStatus();

            _statusTimer = new Timer();
            _statusTimer.Interval = 2000;
            _statusTimer.Tick += delegate { SafeUpdateStatus(); };
            _statusTimer.Start();
        }

        // ---------------------------------------------------------------- DPI

        private float DetectScale()
        {
            // 注意：构造阶段窗口句柄还没创建，Control.DeviceDpi 这时会返回 96，
            // 直接用它会导致整个窗口按 96dpi 画在 200% 的屏幕上 —— 小得看不清。
            // 先问屏幕 DC 要真实 DPI，再退回 DeviceDpi。
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    if (g.DpiX > 0) { return g.DpiX / 96f; }
                }
            }
            catch { }
            try
            {
                int dpi = DeviceDpi;
                if (dpi > 0) { return dpi / 96f; }
            }
            catch { }
            return 1f;
        }

        private int P(int v) { return (int)Math.Round(v * _s); }

        /// <summary>
        /// 自己算居中。FormStartPosition.CenterScreen 是在 DPI 缩放前后混着算的，
        /// 200% 缩放下会把窗口摆到偏右的位置，所以干脆手动摆一次。
        /// </summary>
        private void CenterOnScreen()
        {
            try
            {
                Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(
                    wa.Left + Math.Max(0, (wa.Width - Width) / 2),
                    wa.Top + Math.Max(0, (wa.Height - Height) / 2));
            }
            catch
            {
                StartPosition = FormStartPosition.CenterScreen;
            }
        }

        // ---------------------------------------------------------------- 构建

        private void BuildUi()
        {
            SuspendLayout();

            Text = "PowerSwitch";
            Icon = AppIcon.ForEngine(_engine);   // 与托盘图标保持同一份
            ClientSize = new Size(P(560), P(846));
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;                      // 从托盘弹出，要盖在其他窗口之上
            AutoScaleMode = AutoScaleMode.None;  // 缩放自己做，避免和 WinForms 重复放大
            BackColor = CBg;
            Font = _fN;
            ForeColor = CMain;

            // ---------- 当前状态 ----------
            GroupBox gbStatus = NewGroup("当前状态", 14, 10, 532, 96);
            _lMode = NewLabel("当前模式：--", 16, 20, 190, 24, _fH, CMain);
            _lCpu = NewLabel("CPU：--", 210, 20, 160, 24, _fN, CMain);
            _lMem = NewLabel("内存：--", 366, 20, 150, 24, _fN, CMain);
            _lAuto = NewLabel("自动切换：--", 16, 50, 190, 24, _fN, CMain);
            _lJudge = NewLabel("判定：--", 210, 50, 160, 24, _fN, CMain);
            _lSince = NewLabel("持续：--", 364, 50, 164, 24, _fN, CMain);
            gbStatus.Controls.AddRange(new Control[] { _lMode, _lCpu, _lMem, _lAuto, _lJudge, _lSince });

            // ---------- 手动切换 ----------
            Button btnSaver = NewButton("节能", 14, 114, 100, 32, _fN, CGreen);
            btnSaver.Click += delegate { ForceSwitch(PowerScheme.Saver); };
            Button btnBalanced = NewButton("平衡", 122, 114, 100, 32, _fN, CBlue);
            btnBalanced.Click += delegate { ForceSwitch(PowerScheme.Balanced); };
            Button btnHigh = NewButton("高性能", 230, 114, 100, 32, _fN, CRed);
            btnHigh.Click += delegate { ForceSwitch(PowerScheme.HighPerformance); };
            Button btnCheck = NewButton("立即检查一次", 346, 114, 200, 32, _fN, CBlue);
            btnCheck.Click += OnCheckNow;

            // ---------- 切换条件 ----------
            GroupBox gbCond = NewGroup("切换条件", 14, 154, 532, 186);

            gbCond.Controls.Add(NewLabel("CPU 阈值（%）", 16, 24, 150, 26, _fN, CLabel));
            _nudCpu = NewNum(176, 24, 72, 1, 99, 1);
            gbCond.Controls.Add(_nudCpu);
            gbCond.Controls.Add(NewLabel("低于该值判定为低负载", 258, 24, 250, 26, _fS, CSub));

            gbCond.Controls.Add(NewLabel("低负载持续（分钟）", 16, 56, 150, 26, _fN, CLabel));
            _nudLow = NewNum(176, 56, 72, 1, 240, 1);
            gbCond.Controls.Add(_nudLow);
            gbCond.Controls.Add(NewLabel("满额后切到「低负载方案」", 258, 56, 250, 26, _fS, CSub));

            gbCond.Controls.Add(NewLabel("高负载持续（分钟）", 16, 88, 150, 26, _fN, CLabel));
            _nudHigh = NewNum(176, 88, 72, 1, 240, 1);
            gbCond.Controls.Add(_nudHigh);
            gbCond.Controls.Add(NewLabel("满额后切到「高负载方案」", 258, 88, 250, 26, _fS, CSub));

            gbCond.Controls.Add(NewLabel("夜间时段（小时）", 16, 120, 150, 26, _fN, CLabel));
            _nudNightStart = NewNum(176, 120, 72, 0, 23, 1);
            gbCond.Controls.Add(_nudNightStart);
            gbCond.Controls.Add(NewLabel("点 至", 252, 120, 44, 26, _fN, CLabel));
            _nudNightEnd = NewNum(296, 120, 72, 0, 23, 1);
            gbCond.Controls.Add(_nudNightEnd);
            gbCond.Controls.Add(NewLabel("点", 372, 120, 40, 26, _fN, CLabel));

            _chkAllDay = NewCheck("全天监控（忽略上面的时段，24 小时都按负载切换）", 16, 152, 500);
            gbCond.Controls.Add(_chkAllDay);

            // ---------- 模式映射 ----------
            GroupBox gbMap = NewGroup("模式映射（各场景使用的电源方案）", 14, 348, 532, 118);

            gbMap.Controls.Add(NewLabel("白天（非夜间时段）", 16, 24, 170, 26, _fN, CLabel));
            _cmbDay = NewCombo(196, 24, 130);
            gbMap.Controls.Add(_cmbDay);

            gbMap.Controls.Add(NewLabel("夜间 · 低负载持续后", 16, 56, 170, 26, _fN, CLabel));
            _cmbNightLow = NewCombo(196, 56, 130);
            gbMap.Controls.Add(_cmbNightLow);

            gbMap.Controls.Add(NewLabel("夜间 · 高负载持续后", 16, 88, 170, 26, _fN, CLabel));
            _cmbNightHigh = NewCombo(196, 88, 130);
            gbMap.Controls.Add(_cmbNightHigh);

            // ---------- 远程桌面 ----------
            GroupBox gbRdp = NewGroup("远程桌面", 14, 474, 532, 118);

            _chkRdp = NewCheck("远程连接时锁定方案并暂停自动切换", 16, 20, 480);
            _chkRdp.CheckedChanged += delegate { SyncRdpEnabled(); };
            gbRdp.Controls.Add(_chkRdp);

            gbRdp.Controls.Add(NewLabel("连接时锁定方案", 16, 52, 170, 26, _fN, CLabel));
            _cmbRdpMode = NewCombo(196, 52, 130);
            gbRdp.Controls.Add(_cmbRdpMode);

            gbRdp.Controls.Add(NewLabel("检测间隔（秒）", 16, 84, 170, 26, _fN, CLabel));
            _nudRdpPoll = NewNum(196, 84, 72, 1, 60, 1);
            gbRdp.Controls.Add(_nudRdpPoll);
            gbRdp.Controls.Add(NewLabel("1–60 秒，会话事件之外的兜底轮询", 276, 84, 240, 26, _fS, CSub));

            // ---------- 高级 ----------
            GroupBox gbAdv = NewGroup("高级", 14, 600, 532, 152);

            gbAdv.Controls.Add(NewLabel("采样次数", 16, 24, 100, 26, _fN, CLabel));
            _nudSample = NewNum(120, 24, 60, 1, 10, 1);
            gbAdv.Controls.Add(_nudSample);

            gbAdv.Controls.Add(NewLabel("采样间隔（秒）", 200, 24, 116, 26, _fN, CLabel));
            _nudInterval = NewNum(320, 24, 60, 1, 30, 1);
            gbAdv.Controls.Add(_nudInterval);

            gbAdv.Controls.Add(NewLabel("日志上限（MB）", 16, 54, 100, 26, _fN, CLabel));
            _nudLog = NewNum(120, 54, 60, 1, 100, 1);
            gbAdv.Controls.Add(_nudLog);

            gbAdv.Controls.Add(NewLabel("托盘刷新（秒）", 200, 54, 116, 26, _fN, CLabel));
            _nudRefresh = NewNum(320, 54, 60, 5, 300, 5);
            gbAdv.Controls.Add(_nudRefresh);

            _chkAutoStart = NewCheck("开机自动启动（登录 Windows 后自动运行托盘）", 16, 84, 460);
            gbAdv.Controls.Add(_chkAutoStart);

            _chkPause = NewCheck("暂停自动切换（保持当前模式，图标变空心）", 16, 114, 460);
            gbAdv.Controls.Add(_chkPause);

            // ---------- 底部按钮 ----------
            Button btnSave = NewButton("保存并应用", 14, 764, 140, 36, _fB, CBlue);
            btnSave.Click += OnSave;
            Button btnDefault = NewButton("恢复默认", 162, 764, 110, 36, _fN, CMain);
            btnDefault.Click += OnResetDefaults;
            Button btnLog = NewButton("打开日志", 280, 764, 110, 36, _fN, CMain);
            btnLog.Click += delegate { OpenPath(Paths.LogFile); };
            Button btnClose = NewButton("关闭", 446, 764, 100, 36, _fN, CMain);
            btnClose.Click += delegate { Close(); };

            _hint = NewLabel("配置文件：" + Paths.ConfigFile, 16, 806, 530, 30, _fS, CSub);

            Controls.AddRange(new Control[] {
                gbStatus, btnSaver, btnBalanced, btnHigh, btnCheck,
                gbCond, gbMap, gbRdp, gbAdv,
                btnSave, btnDefault, btnLog, btnClose, _hint
            });

            AcceptButton = btnSave;
            CancelButton = btnClose;

            ResumeLayout(false);
        }

        // ---------------------------------------------------------------- 控件工厂

        private static Font MakeFont(float size, FontStyle style)
        {
            string[] names = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" };
            foreach (string n in names)
            {
                try { return new Font(n, size, style); }
                catch { }
            }
            return new Font(FontFamily.GenericSansSerif, size, style);
        }

        private GroupBox NewGroup(string text, int x, int y, int w, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.Location = new Point(P(x), P(y));
            g.Size = new Size(P(w), P(h));
            g.Font = _fG;
            g.ForeColor = CSub;
            g.BackColor = CCard;
            return g;
        }

        private Label NewLabel(string text, int x, int y, int w, int h, Font font, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(P(x), P(y));
            l.Size = new Size(P(w), P(h));
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Font = font;
            l.ForeColor = color;
            // 用不透明背景：透明标签是交给父控件重绘的，GroupBox 重画边框时
            // 偶发把子标签的文字盖掉（表现为"标签只剩前几个字"）。卡片本来就是纯白。
            l.BackColor = CCard;
            return l;
        }

        private NumericUpDown NewNum(int x, int y, int w, int min, int max, int step)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Increment = step;
            n.Font = _fN;
            n.TextAlign = HorizontalAlignment.Right;
            n.BackColor = CCard;

            // 高度不能硬给：这个控件的上下箭头是自绘的，高度超过 PreferredHeight
            // 会让箭头跑到边框外面去。按自然高度垂直居中即可。
            int h = n.PreferredHeight;
            n.Size = new Size(P(w), h);
            n.Location = new Point(P(x), P(y) + Math.Max(0, (P(26) - h) / 2));
            return n;
        }

        private ComboBox NewCombo(int x, int y, int w)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.System;
            c.Font = _fN;
            foreach (string key in PowerScheme.AllNames) { c.Items.Add(new SchemeItem(key)); }

            int h = c.PreferredHeight;
            c.Size = new Size(P(w), h);
            c.Location = new Point(P(x), P(y) + Math.Max(0, (P(26) - h) / 2));
            return c;
        }

        private CheckBox NewCheck(string text, int x, int y, int w)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Location = new Point(P(x), P(y));
            c.Size = new Size(P(w), P(26));
            c.Font = _fN;
            c.ForeColor = CMain;
            c.BackColor = CCard;
            return c;
        }

        private Button NewButton(string text, int x, int y, int w, int h, Font font, Color fore)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(P(x), P(y));
            b.Size = new Size(P(w), P(h));
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = CLine;
            b.FlatAppearance.BorderSize = 1;
            b.BackColor = CCard;
            b.UseVisualStyleBackColor = false;
            b.Font = font;
            b.ForeColor = fore;
            b.Cursor = Cursors.Hand;
            return b;
        }

        private sealed class SchemeItem
        {
            public readonly string Key;
            public SchemeItem(string key) { Key = key; }
            public override string ToString() { return PowerScheme.DisplayName(Key); }
        }

        private static void SelectCombo(ComboBox box, string key)
        {
            string normalized = PowerScheme.NormalizeName(key, PowerScheme.Balanced);
            for (int i = 0; i < box.Items.Count; i++)
            {
                SchemeItem item = box.Items[i] as SchemeItem;
                if (item != null && string.Equals(item.Key, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedIndex = i;
                    return;
                }
            }
            if (box.Items.Count > 0) { box.SelectedIndex = 0; }
        }

        private static string ComboKey(ComboBox box)
        {
            SchemeItem item = box.SelectedItem as SchemeItem;
            return item != null ? item.Key : PowerScheme.Balanced;
        }

        // ---------------------------------------------------------------- 状态条

        private void SafeUpdateStatus()
        {
            try { UpdateStatus(); }
            catch (Exception ex) { Logger.Warn("刷新状态失败: " + Logger.Describe(ex)); }
        }

        private void UpdateStatus()
        {
            AppConfig cfg = _engine.Config;
            string key = _engine.CurrentKey;

            _lMode.Text = "当前模式：" + PowerScheme.DisplayName(key);
            _lMode.ForeColor = SchemeColor(key);

            double cpu = _engine.LastCpu;
            _lCpu.Text = cpu >= 0
                ? "CPU：" + cpu.ToString("F0") + "%（阈值 " + cfg.CpuThreshold + "%）"
                : "CPU：--";

            long mb = 0;
            try { mb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024); }
            catch { }
            _lMem.Text = "内存：" + mb + " MB";

            if (!_engine.IsDriver)
            {
                // 只读实例：多会话共存时，只有一个实例真正驱动电源方案。
                // 这里必须说清楚，否则用户点「节能」没反应会以为是 bug。
                int sid = EngineLeader.LeaderSession;
                _lAuto.Text = "自动切换：只读";
                _lAuto.ForeColor = CSub;
                _lJudge.Text = "判定：" + JudgeText(cfg);
                _lSince.Text = sid > 0 ? "驱动：会话 " + sid : "驱动：其他会话";
            }
            else if (_engine.IsRemoteOverrideActive)
            {
                _lAuto.Text = "自动切换：远程桌面锁定";
                _lAuto.ForeColor = CBlue;
                _lJudge.Text = "判定：远程桌面";
                _lSince.Text = "来源：" + ClientNameOnly(_engine.RemoteDesktopClient);
            }
            else if (_engine.IsPaused)
            {
                _lAuto.Text = "自动切换：已暂停";
                _lAuto.ForeColor = CRed;
                _lJudge.Text = "判定：暂停";
                string until = _engine.PauseUntilText;
                _lSince.Text = until == null ? "恢复：手动" : "恢复：" + until;
            }
            else
            {
                _lAuto.Text = "自动切换：运行中";
                _lAuto.ForeColor = CGreen;
                _lJudge.Text = "判定：" + JudgeText(cfg);
                _lSince.Text = "持续：" + StreakText(cfg, key);
            }
        }

        private string JudgeText(AppConfig cfg)
        {
            string state = _engine.CurrentLoadState;
            bool night = IsNightTime(cfg, DateTime.Now);
            if (!cfg.AllDayMonitor && !night) { return "白天固定"; }
            if (state == "Low") { return "低负载"; }
            if (state == "High") { return "高负载"; }
            return "等待采样";
        }

        private string StreakText(AppConfig cfg, string current)
        {
            string state = _engine.CurrentLoadState;
            if (state == "Low")
            {
                DateTime? since = _engine.LowSince;
                double m = since.HasValue ? (DateTime.Now - since.Value).TotalMinutes : 0;
                if (m < 0) { m = 0; }
                string target = PowerScheme.DisplayName(cfg.NightLowLoadMode);
                if (current == cfg.NightLowLoadMode) { return "已降至 " + target; }
                return string.Format("{0:F1}/{1} 分 → {2}", m, cfg.LowLoadDuration, target);
            }
            if (state == "High")
            {
                DateTime? since = _engine.HighSince;
                double m = since.HasValue ? (DateTime.Now - since.Value).TotalMinutes : 0;
                if (m < 0) { m = 0; }
                string target = PowerScheme.DisplayName(cfg.NightHighLoadMode);
                if (current == cfg.NightHighLoadMode) { return "已升至 " + target; }
                return string.Format("{0:F1}/{1} 分 → {2}", m, cfg.HighLoadDuration, target);
            }
            return "—";
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

        private static Color SchemeColor(string key)
        {
            if (key == PowerScheme.Saver) { return CGreen; }
            if (key == PowerScheme.HighPerformance || key == PowerScheme.UltimatePerformance) { return CRed; }
            if (key == PowerScheme.Balanced) { return CBlue; }
            return CSub;
        }

        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) { return "—"; }
            if (s.Length <= max) { return s; }
            return s.Substring(0, max - 1) + "…";
        }

        /// <summary>"用户@客户端（会话 3）" → "客户端"，只保留来源机器名，避免挤成半截。</summary>
        private static string ClientNameOnly(string describe)
        {
            if (string.IsNullOrEmpty(describe)) { return "—"; }
            int at = describe.IndexOf('@');
            string s = at >= 0 ? describe.Substring(at + 1) : describe;
            int cut = s.IndexOf('（');
            if (cut > 0) { s = s.Substring(0, cut); }
            return Shorten(s, 16);
        }

        // ---------------------------------------------------------------- 载入 / 保存

        private void LoadValues(AppConfig cfg)
        {
            _nudCpu.Value = cfg.CpuThreshold;
            _nudLow.Value = cfg.LowLoadDuration;
            _nudHigh.Value = cfg.HighLoadDuration;
            _nudNightStart.Value = cfg.NightStartHour;
            _nudNightEnd.Value = cfg.NightEndHour;
            _chkAllDay.Checked = cfg.AllDayMonitor;

            SelectCombo(_cmbDay, cfg.DayMode);
            SelectCombo(_cmbNightLow, cfg.NightLowLoadMode);
            SelectCombo(_cmbNightHigh, cfg.NightHighLoadMode);

            _chkRdp.Checked = cfg.PauseOnRemoteDesktop;
            SelectCombo(_cmbRdpMode, cfg.RemoteDesktopMode);
            _nudRdpPoll.Value = cfg.RdpPollSeconds;
            SyncRdpEnabled();

            _nudSample.Value = cfg.SampleCount;
            _nudInterval.Value = cfg.SampleIntervalSec;
            _nudLog.Value = cfg.MaxLogSizeMB;
            _nudRefresh.Value = cfg.TrayRefreshSec;

            _chkAutoStart.Checked = File.Exists(StartupScriptPath);
            _chkPause.Checked = _engine.IsPaused;
        }

        private void SyncRdpEnabled()
        {
            bool on = _chkRdp.Checked;
            _cmbRdpMode.Enabled = on;
            _nudRdpPoll.Enabled = on;
        }

        private void ForceSwitch(string key)
        {
            try
            {
                if (_engine.ForceSwitch(key))
                {
                    SetHint("已手动切换到「" + PowerScheme.DisplayName(key) + "」", CGreen);
                }
                else
                {
                    SetHint("切换失败，请查看日志", CRed);
                }
                UpdateStatus();
            }
            catch (Exception ex)
            {
                SetHint("切换异常：" + ex.Message, CRed);
            }
        }

        private void OnCheckNow(object sender, EventArgs e)
        {
            SetHint("已触发一次检查，结果稍后写入日志…", CSub);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { _engine.Tick(); }
                catch (Exception ex) { Logger.Error("手动检查失败", ex); }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            UpdateStatus();
                            SetHint("检查完成：" + _engine.LastAction, CSub);
                        }
                        catch { }
                    });
                }
                catch { }
            });
        }

        private void OnResetDefaults(object sender, EventArgs e)
        {
            if (MessageBox.Show("恢复所有设置为默认值？\n（还需要点「保存并应用」才会写入配置）",
                    "PowerSwitch", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                return;
            }
            LoadValues(new AppConfig());
            SetHint("已填入默认值，点「保存并应用」后生效", CSub);
        }

        private void OnSave(object sender, EventArgs e)
        {
            AppConfig cfg = new AppConfig();
            cfg.CpuThreshold = (int)_nudCpu.Value;
            cfg.LowLoadDuration = (int)_nudLow.Value;
            cfg.HighLoadDuration = (int)_nudHigh.Value;
            cfg.NightStartHour = (int)_nudNightStart.Value;
            cfg.NightEndHour = (int)_nudNightEnd.Value;
            cfg.AllDayMonitor = _chkAllDay.Checked;

            cfg.DayMode = ComboKey(_cmbDay);
            cfg.NightLowLoadMode = ComboKey(_cmbNightLow);
            cfg.NightHighLoadMode = ComboKey(_cmbNightHigh);

            cfg.PauseOnRemoteDesktop = _chkRdp.Checked;
            cfg.RemoteDesktopMode = ComboKey(_cmbRdpMode);
            cfg.RdpPollSeconds = (int)_nudRdpPoll.Value;

            cfg.SampleCount = (int)_nudSample.Value;
            cfg.SampleIntervalSec = (int)_nudInterval.Value;
            cfg.MaxLogSizeMB = (int)_nudLog.Value;
            cfg.TrayRefreshSec = (int)_nudRefresh.Value;
            cfg.Normalize();

            if (!ConfigStore.Save(Paths.ConfigFile, cfg))
            {
                SetHint("保存失败，请检查运行目录是否可写：" + Paths.ConfigFile, CRed);
                return;
            }

            Logger.Init(Paths.LogFile, cfg.MaxLogSizeMB);
            Logger.Info("配置已保存，热生效");

            // 暂停开关立即生效，不必等下一次保存
            if (_chkPause.Checked && !_engine.IsPaused) { _engine.Pause(null); }
            else if (!_chkPause.Checked && _engine.IsPaused) { _engine.Resume(); }

            ApplyAutoStart(_chkAutoStart.Checked);

            _engine.ReloadConfig();
            UpdateStatus();

            string msg = string.Format(
                "已保存并应用：CPU 阈值 {0}% · 低负载 {1} 分钟 · 高负载 {2} 分钟 · {3}",
                cfg.CpuThreshold, cfg.LowLoadDuration, cfg.HighLoadDuration,
                cfg.AllDayMonitor ? "全天监控 开" : "夜间 " + cfg.NightStartHour + "–" + cfg.NightEndHour + " 点");
            SetHint(msg, CGreen);
        }

        private void SetHint(string text, Color color)
        {
            _hint.Text = text;
            _hint.ForeColor = color;
        }

        // ---------------------------------------------------------------- 开机自启

        private static string StartupScriptPath
        {
            get
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                return Path.Combine(dir, "PowerSwitch.vbs");
            }
        }

        private static void ApplyAutoStart(bool enable)
        {
            try
            {
                string path = StartupScriptPath;
                if (enable)
                {
                    if (File.Exists(path)) { return; }
                    string exe = Application.ExecutablePath;
                    string body =
                        "' PowerSwitch 开机自启（由设置窗口生成）\r\n" +
                        "CreateObject(\"WScript.Shell\").Run \"\"\"" + exe + "\"\"\", 0, False\r\n";
                    File.WriteAllText(path, body, new System.Text.UTF8Encoding(false));
                    Logger.Info("已创建开机自启项: " + path);
                }
                else
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        Logger.Info("已移除开机自启项: " + path);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("设置开机自启失败: " + Logger.Describe(ex));
            }
        }

        private static void OpenPath(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show("文件不存在：" + path, "PowerSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                Logger.Warn("打开路径失败: " + path + " :: " + Logger.Describe(ex));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_statusTimer != null) { _statusTimer.Stop(); _statusTimer.Dispose(); } } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
