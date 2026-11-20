using System;
using System.Drawing;
using System.Windows.Forms;

namespace GoldTicker;

/// <summary>配置入口：数据源、刷新频率、品种代码、快捷键、置顶与启动行为。</summary>
public sealed class SettingsForm : Form
{
    public AppConfig Result { get; private set; }
    public bool RefreshRequested { get; private set; }

    private readonly NumericUpDown _interval;
    private readonly ComboBox _source;
    private readonly ComboBox _series;
    private readonly TextBox _intlSymbol;
    private readonly TextBox _domSymbol;
    private readonly TextBox _hotkey;
    private readonly CheckBox _topMost;
    private readonly CheckBox _startHidden;
    private readonly CheckBox _edgeHide;
    private readonly CheckBox _hoverExpand;

    private int _y = 18;

    private static readonly string[] Sources = { "自动（推荐）", "新浪财经", "腾讯财经", "GoldAPI" };
    private static readonly string[] SourceKeys = { "auto", "sina", "tencent", "goldapi" };

    private const int LabelX = 18;
    private const int InputX = 146;
    private const int InputW = 256;

    public SettingsForm(AppConfig config, bool hotkeyAvailable)
    {
        Result = config.Clone();

        Text = "设置 · 金价";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(420, 492);
        BackColor = Theme.Bg2;
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 9f);

        // ---------------- 刷新间隔 ----------------
        _interval = new NumericUpDown
        {
            Minimum = 3,
            Maximum = 600,
            Value = Math.Clamp(Result.RefreshSeconds, 3, 600),
            BackColor = InputBg,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Bounds = new Rectangle(InputX, _y, 96, 26),
        };
        AddRow("刷新间隔（秒）", _interval);
        AddHint("3–600 秒。接口失败时自动指数退避重试。", 22);

        // ---------------- 数据源 ----------------
        _source = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = InputBg,
            ForeColor = Theme.Text,
            Bounds = new Rectangle(InputX, _y, InputW, 26),
        };
        _source.Items.AddRange(Sources);
        _source.SelectedIndex = Math.Max(0, Array.IndexOf(SourceKeys, Result.SourcePreference));
        AddRow("数据源", _source);

        _intlSymbol = MakeInput(Result.IntlSymbol);
        AddRow("国际品种代码", _intlSymbol);

        _domSymbol = MakeInput(Result.DomSymbol);
        AddRow("国内品种代码", _domSymbol);

        _hotkey = MakeInput(Result.Hotkey);
        _hotkey.Width = 170;
        AddRow("全局快捷键", _hotkey);
        AddHint(hotkeyAvailable
            ? "格式：Ctrl+Alt+G、Ctrl+Shift+F9（必须含修饰键）"
            : "⚠ 当前快捷键注册失败，换一个再保存", 20, hotkeyAvailable ? Theme.Muted : Theme.Amber);

        // ---------------- 显示 ----------------
        _series = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = InputBg,
            ForeColor = Theme.Text,
            Bounds = new Rectangle(InputX, _y, 130, 26),
        };
        _series.Items.AddRange(new object[] { "国际金价", "国内金价" });
        _series.SelectedIndex = Result.ChartSeries == "dom" ? 1 : 0;
        AddRow("走势图默认品种", _series);

        _topMost = new CheckBox
        {
            Text = "窗口总在最前",
            Checked = Result.TopMost,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(InputX, _y),
        };
        Controls.Add(_topMost);
        _y += 28;

        _startHidden = new CheckBox
        {
            Text = "启动时隐藏到托盘",
            Checked = Result.StartHidden,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(InputX, _y),
        };
        Controls.Add(_startHidden);
        _y += 32;

        _edgeHide = new CheckBox
        {
            Text = "拖到屏幕边缘收起为悬浮小窗",
            Checked = Result.EdgeAutoHide,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(InputX, _y),
        };
        Controls.Add(_edgeHide);
        _y += 28;

        _hoverExpand = new CheckBox
        {
            Text = "悬停悬浮小窗时自动展开",
            Checked = Result.MiniHoverExpand,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(InputX, _y),
        };
        Controls.Add(_hoverExpand);
        _y += 30;

        AddHint("国内金价默认取上期所“黄金连续”（元/克），失败时回落至黄金 T+D。", 32, Theme.Muted, 256);

        // ---------------- 按钮 ----------------
        var reset = MakeButton("恢复默认", LabelX, ClientSize.Height - 48, 88);
        reset.Click += (_, _) => ApplyDefaults();

        var cancel = MakeButton("取消", ClientSize.Width - 190, ClientSize.Height - 48, 76);
        cancel.DialogResult = DialogResult.Cancel;

        var save = MakeButton("保存", ClientSize.Width - 100, ClientSize.Height - 48, 82);
        save.BackColor = Color.FromArgb(58, 48, 24);
        save.ForeColor = Theme.Gold;
        save.Click += (_, _) => OnSave();

        Controls.AddRange(new Control[] { reset, cancel, save });

        AcceptButton = save;
        CancelButton = cancel;
    }

    private static Color InputBg => Color.FromArgb(38, 42, 50);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(Handle, Theme.Bg2, Theme.Text);
    }

    private TextBox MakeInput(string value) => new()
    {
        Text = value,
        BackColor = InputBg,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle,
        Bounds = new Rectangle(InputX, _y, InputW, 26),
    };

    private Button MakeButton(string text, int x, int y, int width) => new()
    {
        Text = text,
        Bounds = new Rectangle(x, y, width, 30),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(42, 46, 55),
        ForeColor = Theme.Text,
        FlatAppearance = { BorderColor = Color.FromArgb(62, 68, 80) },
    };

    private void AddRow(string text, Control control)
    {
        var label = new Label
        {
            Text = text,
            ForeColor = Theme.Muted,
            Bounds = new Rectangle(LabelX, _y + 4, 124, 20),
            AutoSize = false,
        };
        Controls.Add(label);
        Controls.Add(control);
        _y += 38;
    }

    private void AddHint(string text, int height, Color? color = null, int width = 256)
    {
        var hint = new Label
        {
            Text = text,
            ForeColor = color ?? Theme.Muted,
            Font = new Font("Microsoft YaHei UI", 8f),
            Bounds = new Rectangle(InputX, _y - 6, width, height),
            AutoSize = false,
        };
        Controls.Add(hint);
        _y += height - 6;
    }

    private void ApplyDefaults()
    {
        var d = new AppConfig();
        _interval.Value = d.RefreshSeconds;
        _source.SelectedIndex = 0;
        _intlSymbol.Text = d.IntlSymbol;
        _domSymbol.Text = d.DomSymbol;
        _hotkey.Text = d.Hotkey;
        _series.SelectedIndex = 0;
        _topMost.Checked = d.TopMost;
        _startHidden.Checked = d.StartHidden;
        _edgeHide.Checked = d.EdgeAutoHide;
        _hoverExpand.Checked = d.MiniHoverExpand;
    }

    private void OnSave()
    {
        var intl = _intlSymbol.Text.Trim();
        var dom = _domSymbol.Text.Trim();
        var hotkey = _hotkey.Text.Trim();

        if (intl.Length == 0 || dom.Length == 0)
        {
            MessageBox.Show(this, "品种代码不能为空。", "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!HotkeyParser.TryParse(hotkey, out _, out _))
        {
            MessageBox.Show(this, "快捷键格式不正确。示例：Ctrl+Alt+G、Ctrl+Shift+F9。",
                "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result.RefreshSeconds = (int)_interval.Value;
        Result.SourcePreference = SourceKeys[Math.Max(0, _source.SelectedIndex)];
        Result.IntlSymbol = intl;
        Result.DomSymbol = dom;
        Result.Hotkey = hotkey;
        Result.ChartSeries = _series.SelectedIndex == 1 ? "dom" : "intl";
        Result.TopMost = _topMost.Checked;
        Result.StartHidden = _startHidden.Checked;
        Result.EdgeAutoHide = _edgeHide.Checked;
        Result.MiniHoverExpand = _hoverExpand.Checked;

        RefreshRequested = true;
        DialogResult = DialogResult.OK;
        Close();
    }
}
