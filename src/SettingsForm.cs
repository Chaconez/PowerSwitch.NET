using System;
using System.Drawing;
using System.Windows.Forms;

namespace PowerSwitch
{
    /// <summary>
    /// 图形化设置窗口。字段与 power_config.json 一一对应，
    /// 保存后立刻热生效（不需要重启托盘，也不需要重装计划任务）。
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private readonly PowerEngine _engine;
        private readonly AppConfig _original;

        private NumericUpDown _nightStart;
        private NumericUpDown _nightEnd;
        private CheckBox _allDay;
        private NumericUpDown _cpuThreshold;
        private NumericUpDown _lowDuration;
        private NumericUpDown _highDuration;
        private NumericUpDown _sampleCount;
        private NumericUpDown _sampleInterval;
        private NumericUpDown _maxLogMb;
        private NumericUpDown _trayRefresh;
        private ComboBox _dayMode;
        private ComboBox _nightLowMode;
        private ComboBox _nightHighMode;

        public SettingsForm(PowerEngine engine)
        {
            _engine = engine;
            _original = engine.Config;

            BuildUi();
            LoadValues(_original);
        }

        private void BuildUi()
        {
            Text = "PowerSwitch 设置";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(470, 520);
            BackColor = Color.White;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 2;
            root.Padding = new Padding(16, 12, 16, 8);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(root);

            int row = 0;

            AddSection(root, ref row, "时段");

            _nightStart = AddNumber(root, ref row, "夜间开始（时）", 0, 23, 1);
            _nightEnd = AddNumber(root, ref row, "夜间结束（时）", 0, 23, 1);
            _allDay = new CheckBox();
            _allDay.Text = "全天监控（忽略夜间时段，始终按负载切换）";
            _allDay.AutoSize = true;
            _allDay.Margin = new Padding(3, 6, 3, 8);
            root.Controls.Add(new Label(), 0, row);
            root.Controls.Add(_allDay, 1, row);
            row++;

            AddSection(root, ref row, "负载判定");

            _cpuThreshold = AddNumber(root, ref row, "CPU 阈值（%）", 1, 99, 1);
            _lowDuration = AddNumber(root, ref row, "低负载持续（分钟）", 1, 240, 1);
            _highDuration = AddNumber(root, ref row, "高负载持续（分钟）", 1, 240, 1);
            _sampleCount = AddNumber(root, ref row, "每次采样次数", 1, 10, 1);
            _sampleInterval = AddNumber(root, ref row, "采样间隔（秒）", 1, 30, 1);

            AddSection(root, ref row, "方案映射");

            _dayMode = AddCombo(root, ref row, "白天方案");
            _nightLowMode = AddCombo(root, ref row, "夜间低负载");
            _nightHighMode = AddCombo(root, ref row, "夜间高负载");

            AddSection(root, ref row, "其它");

            _maxLogMb = AddNumber(root, ref row, "日志上限（MB）", 1, 100, 1);
            _trayRefresh = AddNumber(root, ref row, "托盘刷新（秒）", 5, 300, 5);

            // 底部按钮
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Height = 52;
            buttons.Padding = new Padding(0, 8, 16, 8);
            buttons.BackColor = Color.FromArgb(248, 248, 248);

            Button btnSave = new Button();
            btnSave.Text = "保存";
            btnSave.Width = 88;
            btnSave.Height = 30;
            btnSave.DialogResult = DialogResult.None;
            btnSave.Click += OnSave;

            Button btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Width = 88;
            btnCancel.Height = 30;
            btnCancel.DialogResult = DialogResult.Cancel;

            Button btnReset = new Button();
            btnReset.Text = "恢复默认";
            btnReset.Width = 96;
            btnReset.Height = 30;
            btnReset.Click += OnResetDefaults;

            buttons.Controls.Add(btnSave);
            buttons.Controls.Add(btnCancel);
            buttons.Controls.Add(btnReset);

            Controls.Add(buttons);
            AcceptButton = btnSave;
            CancelButton = btnCancel;
        }

        private void AddSection(TableLayoutPanel root, ref int row, string title)
        {
            Label lbl = new Label();
            lbl.Text = title;
            lbl.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lbl.ForeColor = Color.FromArgb(60, 60, 60);
            lbl.AutoSize = true;
            lbl.Margin = new Padding(3, row == 0 ? 2 : 14, 3, 4);
            root.Controls.Add(lbl, 0, row);
            root.SetColumnSpan(lbl, 2);
            row++;
        }

        private NumericUpDown AddNumber(TableLayoutPanel root, ref int row, string label, int min, int max, int step)
        {
            Label lbl = new Label();
            lbl.Text = label;
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.AutoSize = true;
            lbl.Margin = new Padding(10, 7, 3, 3);

            NumericUpDown num = new NumericUpDown();
            num.Minimum = min;
            num.Maximum = max;
            num.Increment = step;
            num.Width = 96;
            num.Margin = new Padding(3, 4, 3, 3);
            num.TextAlign = HorizontalAlignment.Right;

            root.Controls.Add(lbl, 0, row);
            root.Controls.Add(num, 1, row);
            row++;
            return num;
        }

        private ComboBox AddCombo(TableLayoutPanel root, ref int row, string label)
        {
            Label lbl = new Label();
            lbl.Text = label;
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.AutoSize = true;
            lbl.Margin = new Padding(10, 7, 3, 3);

            ComboBox box = new ComboBox();
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            box.Width = 180;
            box.Margin = new Padding(3, 4, 3, 3);
            foreach (string key in PowerScheme.AllNames)
            {
                box.Items.Add(new SchemeItem(key));
            }

            root.Controls.Add(lbl, 0, row);
            root.Controls.Add(box, 1, row);
            row++;
            return box;
        }

        private void LoadValues(AppConfig cfg)
        {
            _nightStart.Value = cfg.NightStartHour;
            _nightEnd.Value = cfg.NightEndHour;
            _allDay.Checked = cfg.AllDayMonitor;
            _cpuThreshold.Value = cfg.CpuThreshold;
            _lowDuration.Value = cfg.LowLoadDuration;
            _highDuration.Value = cfg.HighLoadDuration;
            _sampleCount.Value = cfg.SampleCount;
            _sampleInterval.Value = cfg.SampleIntervalSec;
            _maxLogMb.Value = cfg.MaxLogSizeMB;
            _trayRefresh.Value = cfg.TrayRefreshSec;
            SelectCombo(_dayMode, cfg.DayMode);
            SelectCombo(_nightLowMode, cfg.NightLowLoadMode);
            SelectCombo(_nightHighMode, cfg.NightHighLoadMode);
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

        private void OnResetDefaults(object sender, EventArgs e)
        {
            if (MessageBox.Show("恢复所有设置为默认值？", "PowerSwitch", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                return;
            }
            LoadValues(new AppConfig());
        }

        private void OnSave(object sender, EventArgs e)
        {
            AppConfig cfg = new AppConfig();
            cfg.NightStartHour = (int)_nightStart.Value;
            cfg.NightEndHour = (int)_nightEnd.Value;
            cfg.AllDayMonitor = _allDay.Checked;
            cfg.CpuThreshold = (int)_cpuThreshold.Value;
            cfg.LowLoadDuration = (int)_lowDuration.Value;
            cfg.HighLoadDuration = (int)_highDuration.Value;
            cfg.SampleCount = (int)_sampleCount.Value;
            cfg.SampleIntervalSec = (int)_sampleInterval.Value;
            cfg.MaxLogSizeMB = (int)_maxLogMb.Value;
            cfg.TrayRefreshSec = (int)_trayRefresh.Value;
            cfg.DayMode = ComboKey(_dayMode);
            cfg.NightLowLoadMode = ComboKey(_nightLowMode);
            cfg.NightHighLoadMode = ComboKey(_nightHighMode);
            cfg.Normalize();

            if (!ConfigStore.Save(Paths.ConfigFile, cfg))
            {
                MessageBox.Show("保存失败，请检查运行目录是否可写：\n" + Paths.ConfigFile, "PowerSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Logger.Init(Paths.LogFile, cfg.MaxLogSizeMB);
            Logger.Info("配置已保存，热生效");
            _engine.ReloadConfig();
            DialogResult = DialogResult.OK;
            Close();
        }

        private sealed class SchemeItem
        {
            public readonly string Key;
            public SchemeItem(string key) { Key = key; }
            public override string ToString() { return PowerScheme.DisplayName(Key); }
        }
    }
}
