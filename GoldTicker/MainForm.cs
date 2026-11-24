using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace GoldTicker;

/// <summary>
/// 主窗口：无子控件，全部自绘。窗口高度决定信息层级：
/// 极矮 = 只显示当前价格；较矮 = 价格 + 状态；较高 = 追加走势图，时间跨度按绘图区面积自适应。
/// </summary>
public sealed class MainForm : Form
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4A11;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const int SW_SHOWNORMAL = 1;

    private readonly QuoteFetcher _fetcher = new();
    private readonly List<Sample> _histIntl;
    private readonly List<Sample> _histDom;
    private readonly List<HitArea> _hits = new();

    private readonly Font _fLabel;
    private readonly Font _fSmall;
    private readonly Font _fTick;
    private readonly Font _fPrice;
    private readonly Font _fPrice2;
    private readonly Font _fUnit;
    private readonly Font _fChange;
    private readonly Font _fChip;
    private readonly Font _fIcon;
    private readonly Font _fInline;

    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _miShow;
    private readonly ToolStripMenuItem _miTop;

    private AppConfig _cfg;

    private Quote? _intl;
    private Quote? _dom;
    private string? _errIntl;
    private string? _errDom;

    private bool _busy;
    private int _failCount;
    private int _spin;
    private DateTime _nextFetchAt = DateTime.MinValue;
    private DateTime _nextHistAt = DateTime.MinValue;
    private DateTime _lastAttempt = DateTime.MinValue;

    private string _chartKey = "intl";
    private string _statusCache = "";
    private Point _mouse = new(-1, -1);
    private bool _hotkeyOk;

    private MiniForm? _mini;
    private bool _docked;
    private bool _suppressDock;
    private Rectangle _restoreBounds;
    private readonly System.Windows.Forms.Timer _dockTimer;
    private DateTime _dockEnabledAt = DateTime.MaxValue;

    // 悬浮小窗单击或悬停后「临时展开」：3 秒宽限期内保持，之后鼠标离开 / 窗口失焦就收回悬浮小窗
    private DockEdge _miniEdge = DockEdge.Left;
    private bool _tempMode;
    private DateTime _tempDeadline = DateTime.MinValue;
    private bool _tempHadFocus;
    private bool _tempLostFocus;
    private readonly System.Windows.Forms.Timer _tempTimer;

    public MainForm()
    {
        _cfg = AppConfig.Load();
        _chartKey = _cfg.ChartSeries == "dom" ? "dom" : "intl";
        _histIntl = HistoryStore.Load("intl");
        _histDom = HistoryStore.Load("dom");

        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "金价";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        _fLabel = new Font("Microsoft YaHei UI", 8.5f);
        _fSmall = new Font("Microsoft YaHei UI", 8f);
        _fTick = new Font("Microsoft YaHei UI", 7.5f);
        _fPrice = new Font("Microsoft YaHei UI", 22f, FontStyle.Bold);
        _fPrice2 = new Font("Microsoft YaHei UI", 15.5f, FontStyle.Bold);
        _fUnit = new Font("Microsoft YaHei UI", 8.5f);
        _fChange = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        _fChip = new Font("Microsoft YaHei UI", 8.5f);
        _fIcon = new Font("Segoe MDL2 Assets", 10f);
        _fInline = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold);

        Icon = AppIconFactory.Create(32);

        // ---------------- 托盘 ----------------
        var menu = new ContextMenuStrip();
        _miShow = new ToolStripMenuItem("隐藏窗口", null, (_, _) => ToggleVisible());
        var miRefresh = new ToolStripMenuItem("立即刷新", null, (_, _) => ForceRefresh());
        _miTop = new ToolStripMenuItem("窗口置顶", null, (_, _) => ToggleTopMost());
        var miMini = new ToolStripMenuItem("收起为悬浮小窗", null, (_, _) => ToggleMini());
        var miSettings = new ToolStripMenuItem("设置…", null, (_, _) => OpenSettings());
        var miExit = new ToolStripMenuItem("退出", null, (_, _) => Close());
        menu.Items.AddRange(new ToolStripItem[]
        {
            _miShow, miMini, miRefresh, _miTop, new ToolStripSeparator(), miSettings, new ToolStripSeparator(), miExit,
        });
        menu.Opening += (_, _) =>
        {
            _miShow.Text = _docked || Visible ? "隐藏窗口" : "显示窗口";
            _miTop.Checked = _cfg.TopMost;
            miMini.Text = _docked ? "展开完整窗口" : "收起为悬浮小窗";
        };

        _tray = new NotifyIcon
        {
            Visible = true,
            Icon = AppIconFactory.Create(16),
            Text = "金价 · 正在加载…",
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleVisible();
        };
        _tray.DoubleClick += (_, _) => ToggleVisible();

        _uiTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _uiTimer.Tick += OnTick;
        _uiTimer.Start();

        ResizeEnd += (_, _) => TryAutoDock();

        // 拖动/吸附/程序化移动后都判断一次是否贴边（拖动过程中不处理，松手再收）
        _dockTimer = new System.Windows.Forms.Timer { Interval = 450 };
        _dockTimer.Tick += (_, _) =>
        {
            _dockTimer.Stop();
            if (Control.MouseButtons != MouseButtons.None) { _dockTimer.Start(); return; }
            TryAutoDock();
        };
        LocationChanged += (_, _) => QueueDockCheck();
        SizeChanged += (_, _) => QueueDockCheck();

        _tempTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _tempTimer.Tick += OnTempTick;

        Activated += (_, _) =>
        {
            if (!_tempMode) return;
            _tempHadFocus = true;
            _tempLostFocus = false;
        };
        Deactivate += (_, _) =>
        {
            if (_tempMode && _tempHadFocus) _tempLostFocus = true;
        };
    }

    private void QueueDockCheck()
    {
        if (_suppressDock || _docked) return;
        _dockTimer.Stop();
        _dockTimer.Start();
    }

    // ------------------------------------------------------------------ 生命周期

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        DarkTitleBar.Apply(Handle, Theme.Bg, Theme.Text);

        MinimumSize = new Size(Sc(250), Sc(124));
        RegisterHotkey();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        var bounds = new Rectangle(_cfg.WinX, _cfg.WinY, _cfg.WinW, _cfg.WinH);
        var hasPosition = _cfg.WinX != int.MinValue && _cfg.WinY != int.MinValue;
        if (!hasPosition ||
            bounds.Width < Sc(250) || bounds.Height < Sc(124) ||
            !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
        {
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
            var w = Math.Clamp(_cfg.WinW, Sc(250), Math.Max(Sc(250), wa.Width - Sc(40)));
            var h = Math.Clamp(_cfg.WinH, Sc(124), Math.Max(Sc(124), wa.Height - Sc(80)));
            bounds = new Rectangle(wa.Right - w - Sc(24), wa.Top + Sc(48), w, h);
        }

        Bounds = bounds;
        TopMost = _cfg.TopMost;
        _lastAttempt = DateTime.MinValue;
        _nextFetchAt = DateTime.MinValue;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_cfg.StartHidden)
        {
            Hide();
            return;
        }

        // 有些启动器/快捷方式会用 SW_HIDE 拉起进程，这时窗口虽然"已显示"却是隐藏的，
        // 这里主动覆盖一次，避免出现"点了没反应"的假死。
        ForceShow();
        BeginInvoke(new Action(ForceShow));
        _dockEnabledAt = DateTime.Now.AddSeconds(2);
        Log.Write("shown, visible=" + IsWindowVisible(Handle));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        MinimumSize = new Size(Sc(250), Sc(124));
        Invalidate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveState();
        UnregisterHotKey(Handle, HotkeyId);
        _uiTimer.Stop();
        _tempTimer.Stop();
        if (_mini != null)
        {
            _mini.Dispose();
            _mini = null;
        }
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tempTimer?.Dispose();
            _fLabel.Dispose();
            _fSmall.Dispose();
            _fTick.Dispose();
            _fPrice.Dispose();
            _fPrice2.Dispose();
            _fUnit.Dispose();
            _fChange.Dispose();
            _fChip.Dispose();
            _fIcon.Dispose();
            _fInline.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            ToggleVisible();
            return;
        }
        base.WndProc(ref m);
    }

    private void SaveState()
    {
        if (WindowState == FormWindowState.Normal)
        {
            _cfg.WinX = Bounds.X;
            _cfg.WinY = Bounds.Y;
            _cfg.WinW = Bounds.Width;
            _cfg.WinH = Bounds.Height;
        }
        _cfg.ChartSeries = _chartKey;
        _cfg.Save();

        var intl = _histIntl.ToList();
        var dom = _histDom.ToList();
        HistoryStore.Save("intl", intl);
        HistoryStore.Save("dom", dom);
    }

    // ------------------------------------------------------------------ 热键 / 显示状态

    private void RegisterHotkey()
    {
        if (!IsHandleCreated) return;
        UnregisterHotKey(Handle, HotkeyId);
        _hotkeyOk = HotkeyParser.TryParse(_cfg.Hotkey, out var mods, out var vk) &&
                    RegisterHotKey(Handle, HotkeyId, mods, vk);
        Log.Write("hotkey " + _cfg.Hotkey + " registered=" + _hotkeyOk);
    }

    private void ToggleVisible()
    {
        if (_docked)
        {
            ExpandFromMini(true);
            return;
        }

        if (Visible && WindowState != FormWindowState.Minimized)
        {
            HideToTray();
            return;
        }

        ShowFromOutside();
    }

    /// <summary>托盘菜单：在悬浮小窗与完整窗口之间切换。</summary>
    private void ToggleMini()
    {
        if (_docked) ExpandFromMini(true);
        else CollapseToMini(DockEdge.Left);
    }

    /// <summary>把窗口叫到前台：托盘、快捷键、以及"再次双击图标"都走这里。</summary>
    public void ShowFromOutside()
    {
        if (_docked)
        {
            ExpandFromMini(true);
            return;
        }

        if (!IsHandleCreated) return;

        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (!Visible) Show();

        ForceShow();
        TopMost = _cfg.TopMost;
        Activate();
        BringToFront();
        Log.Write("show requested, visible=" + IsWindowVisible(Handle));
    }

    private void ForceShow()
    {
        if (!IsHandleCreated) return;
        ShowWindow(Handle, SW_SHOWNORMAL);
        SetForegroundWindow(Handle);
    }

    // ------------------------------------------------------------------ 贴边悬浮小窗

    /// <summary>拖到屏幕左/右边缘松手后，自动收起为悬浮小窗。</summary>
    private void TryAutoDock()
    {
        if (!_cfg.EdgeAutoHide || _docked || _suppressDock) return;
        if (DateTime.Now < _dockEnabledAt) return;   // 启动 / 恢复位置时不误收
        if (!Visible || WindowState != FormWindowState.Normal) return;

        var wa = Screen.FromControl(this).WorkingArea;
        var d = Sc(_cfg.EdgeSnapDistance);

        if (Bounds.Left <= wa.Left + d) CollapseToMini(DockEdge.Left);
        else if (Bounds.Right >= wa.Right - d) CollapseToMini(DockEdge.Right);
    }

    private void CollapseToMini(DockEdge edge) => CollapseToMini(edge, Bounds);

    private void CollapseToMini(DockEdge edge, Rectangle anchor)
    {
        if (_mini != null) return;

        CancelTempExpand();
        _dockTimer.Stop();

        var wa = Screen.FromRectangle(anchor).WorkingArea;
        _restoreBounds = anchor;
        _miniEdge = edge;

        var mini = new MiniForm
        {
            HoverExpand = _cfg.MiniHoverExpand,
            TopMost = _cfg.TopMost,
        };
        mini.SetQuotes(_intl, _dom);
        mini.ExpandRequested += OnMiniExpand;
        mini.MovedByUser += OnMiniMoved;
        mini.DockTo(wa, edge, anchor);
        mini.Show();

        _mini = mini;
        _docked = true;
        Hide();
        Log.Write("collapsed to mini (" + edge + ")");
    }

    /// <summary>单击 / 悬停悬浮小窗：都是临时展开完整窗口（3 秒后失焦自动收回）。</summary>
    private void OnMiniExpand() => ExpandFromMini(true, true);

    private void OnMiniMoved(Point location)
    {
        var mini = _mini;
        if (mini == null) return;

        var wa = Screen.FromPoint(location).WorkingArea;
        var d = Sc(_cfg.EdgeSnapDistance);
        var nearLeft = location.X <= wa.Left + d;
        var nearRight = location.X + mini.Width >= wa.Right - d;

        _restoreBounds = new Rectangle(location.X, location.Y, _restoreBounds.Width, _restoreBounds.Height);

        if (nearLeft || nearRight)
        {
            _miniEdge = nearLeft ? DockEdge.Left : DockEdge.Right;
            mini.DockTo(wa, _miniEdge, mini.Bounds);
        }
        else
        {
            ExpandFromMini(true);
        }
    }

    private void ExpandFromMini(bool pinned, bool temporary = false)
    {
        var mini = _mini;
        if (mini == null) return;

        _mini = null;
        _docked = false;

        mini.ExpandRequested -= OnMiniExpand;
        mini.MovedByUser -= OnMiniMoved;
        mini.Hide();
        mini.Dispose();

        _suppressDock = true;
        try
        {
            var b = _restoreBounds;
            var wa = Screen.FromRectangle(b).WorkingArea;
            b.Width = Math.Max(Sc(250), b.Width);
            b.Height = Math.Max(Sc(124), b.Height);
            b.X = Math.Clamp(b.X, wa.Left, Math.Max(wa.Left, wa.Right - b.Width));
            b.Y = Math.Clamp(b.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - b.Height));

            // 从贴边状态展开时，把窗口往里挪一点，免得用户轻轻一拖又立刻收起
            var pad = Sc(_cfg.EdgeSnapDistance) + Sc(12);
            if (b.X <= wa.Left + Sc(_cfg.EdgeSnapDistance)) b.X = wa.Left + pad;
            if (b.Right >= wa.Right - Sc(_cfg.EdgeSnapDistance)) b.X = wa.Right - pad - b.Width;

            Bounds = b;
        }
        finally
        {
            _suppressDock = false;
        }

        Show();
        TopMost = _cfg.TopMost;
        ForceShow();
        Log.Write("expanded from mini, pinned=" + pinned + (temporary ? ", temporary" : ""));

        if (temporary) BeginTempExpand();
        else CancelTempExpand();
    }

    // ------------------------------------------------------------------ 临时展开（单击 / 悬停小窗）

    private void BeginTempExpand()
    {
        _tempMode = true;
        _tempHadFocus = IsSelfForeground() || ContainsFocus;
        _tempLostFocus = false;
        _tempDeadline = DateTime.Now.AddSeconds(3);
        _tempTimer.Start();
    }

    private void CancelTempExpand()
    {
        _tempMode = false;
        _tempTimer.Stop();
    }

    private void OnTempTick(object? sender, EventArgs e)
    {
        if (!_tempMode || _mini != null)
        {
            _tempTimer.Stop();
            return;
        }

        if (!Visible)
        {
            CancelTempExpand();
            return;
        }

        if (DateTime.Now < _tempDeadline) return;   // 3 秒宽限期，刚展开时鼠标多半还在卡片上

        var cursorInside = Bounds.Contains(Cursor.Position);
        var focused = IsSelfForeground() || ContainsFocus;

        // 鼠标离开窗口，或窗口失去前台焦点，就收回悬浮小窗
        if (!cursorInside || (_tempHadFocus && _tempLostFocus && !focused))
        {
            Log.Write("temp collapse: inside=" + cursorInside + " hadFocus=" + _tempHadFocus +
                      " lost=" + _tempLostFocus + " focused=" + focused);
            CollapseFromTemp();
        }
    }

    private void CollapseFromTemp()
    {
        CancelTempExpand();
        if (_mini != null) return;

        // 回到展开前的锚点，而不是临时窗口此刻的位置
        CollapseToMini(_miniEdge, _restoreBounds);
        Log.Write("temp expand ended, back to mini");
    }

    private bool IsSelfForeground()
    {
        if (!IsHandleCreated) return false;
        try { return GetForegroundWindow() == Handle; } catch { return false; }
    }

    private void HideToTray()
    {
        CancelTempExpand();
        Hide();
        var tip = _hotkeyOk ? "已隐藏到托盘，" + _cfg.Hotkey + " 可唤出" : "已隐藏到托盘，可从托盘图标恢复";
        try { _tray.ShowBalloonTip(1800, "金价", tip, ToolTipIcon.None); } catch { }
    }

    private void ToggleTopMost()
    {
        _cfg.TopMost = !_cfg.TopMost;
        TopMost = _cfg.TopMost;
        _cfg.Save();
        Invalidate();
    }

    private void ForceRefresh()
    {
        _failCount = 0;
        _nextFetchAt = DateTime.MinValue;
        _nextHistAt = DateTime.MinValue;
        if (!_busy) _ = RefreshAsync();
    }

    // ------------------------------------------------------------------ 抓取

    private void OnTick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            _spin = (_spin + 36) % 360;
            Invalidate();
            return;
        }

        if (DateTime.Now >= _nextFetchAt)
        {
            _ = RefreshAsync();
            return;
        }

        var text = BuildStatusText();
        if (text != _statusCache)
        {
            _statusCache = text;
            var layout = ComputeLayout();
            Invalidate(Rectangle.Round(layout.Status));
        }
    }

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        _lastAttempt = DateTime.Now;
        _statusCache = "";
        Invalidate();

        var intlTask = SafeQuote(() => _fetcher.FetchIntlAsync(_cfg, CancellationToken.None));
        var domTask = SafeQuote(() => _fetcher.FetchDomAsync(_cfg, CancellationToken.None));

        var (intl, intlErr) = await intlTask;
        var (dom, domErr) = await domTask;

        if (intl != null)
        {
            _intl = intl;
            AddSample(_histIntl, intl.Price, intl.QuoteTime);
            _errIntl = null;
        }
        else
        {
            _errIntl = intlErr;
        }

        if (dom != null)
        {
            _dom = dom;
            AddSample(_histDom, dom.Price, dom.QuoteTime);
            _errDom = null;
        }
        else
        {
            _errDom = domErr;
        }

        var anyOk = intl != null || dom != null;
        _failCount = anyOk ? 0 : Math.Min(_failCount + 1, 6);

        Log.Write("quote intl=" + (intl != null ? intl.Price.ToString("0.###") + "/" + intl.Source : "ERR " + intlErr) +
                  " dom=" + (dom != null ? dom.Price.ToString("0.###") + "/" + dom.Source : "ERR " + domErr));

        // 失败时指数退避，最长 3 分钟，避免网络断了以后每秒打接口
        var delay = anyOk
            ? _cfg.RefreshSeconds
            : Math.Min(_cfg.RefreshSeconds * Math.Pow(2, _failCount - 1), 180);
        _nextFetchAt = DateTime.Now.AddSeconds(delay);

        _busy = false;
        UpdateTrayText();
        _mini?.SetQuotes(_intl, _dom);
        Invalidate();

        if (DateTime.Now >= _nextHistAt) _ = RefreshHistoryAsync();
    }

    private static async System.Threading.Tasks.Task<(Quote? Quote, string? Error)> SafeQuote(Func<System.Threading.Tasks.Task<Quote>> factory)
    {
        try
        {
            return (await factory().ConfigureAwait(true), null);
        }
        catch (FetchException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex)
        {
            return (null, "数据解析失败：" + ex.Message);
        }
    }

    private async System.Threading.Tasks.Task RefreshHistoryAsync()
    {
        _nextHistAt = DateTime.Now.AddMinutes(3);

        try
        {
            var intl = await SafeHistory(() => _fetcher.FetchIntlHistoryAsync(_cfg.IntlSymbol, CancellationToken.None));
            if (intl != null) Merge(_histIntl, intl);
            Log.Write("history intl: " + (intl == null ? "ERR" : intl.Count + " pts") + ", total " + _histIntl.Count);

            var dom = await SafeHistory(() => _fetcher.FetchDomHistoryAsync(_cfg.DomSymbol, CancellationToken.None));
            if (dom != null) Merge(_histDom, dom);
            Log.Write("history dom: " + (dom == null ? "ERR" : dom.Count + " pts") + ", total " + _histDom.Count);
        }
        catch (Exception ex)
        {
            // 历史接口失败不影响实时报价，静默降级为本地采样
            Log.Write("history failed: " + ex.Message);
        }

        var i = _histIntl.ToList();
        var d = _histDom.ToList();
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            HistoryStore.Save("intl", i);
            HistoryStore.Save("dom", d);
        });

        Invalidate();
    }

    private static async System.Threading.Tasks.Task<List<Sample>?> SafeHistory(Func<System.Threading.Tasks.Task<List<Sample>>> factory)
    {
        try
        {
            return await factory().ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    private static void AddSample(List<Sample> list, double price, DateTime time)
    {
        if (price <= 0) return;
        var bucket = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0);

        for (var i = list.Count - 1; i >= 0 && i > list.Count - 240; i--)
        {
            if (list[i].T == bucket)
            {
                list[i].P = price;
                return;
            }
        }

        list.Add(new Sample { T = bucket, P = price });
        list.Sort((a, b) => a.T.CompareTo(b.T));
        Prune(list);
    }

    private static void Merge(List<Sample> target, List<Sample> incoming)
    {
        var map = new Dictionary<long, double>(target.Count + incoming.Count);
        foreach (var s in target) map[s.T.Ticks] = s.P;
        foreach (var s in incoming)
            if (s.P > 0) map[s.T.Ticks] = s.P;

        target.Clear();
        foreach (var kv in map.OrderBy(k => k.Key))
            target.Add(new Sample { T = new DateTime(kv.Key), P = kv.Value });
        Prune(target);
    }

    private static void Prune(List<Sample> list)
    {
        var cut = DateTime.Now.AddHours(-30);
        list.RemoveAll(s => s.T < cut);
        if (list.Count > 6000) list.RemoveRange(0, list.Count - 6000);
    }

    private void UpdateTrayText()
    {
        try
        {
            var p = _intl?.Price ?? _dom?.Price;
            _tray.Text = p is null
                ? "金价 · 暂无数据"
                : "金价 " + Fmt.Price(p.Value) + (_intl != null ? " USD/oz" : " 元/克");
        }
        catch
        {
            // 托盘文本超长会抛异常，忽略即可
        }
    }

    // ------------------------------------------------------------------ 布局

    private struct PanelLayout
    {
        public bool Inline;
        public bool ShowChart;
        public double Hours;
        public RectangleF Header;
        public RectangleF Chart;
        public RectangleF Status;
        public RectangleF Chips;
        public RectangleF Plot;
    }

    private float ScF(float v) => v * DeviceDpi / 96f;
    private int Sc(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    private PanelLayout ComputeLayout()
    {
        var layout = new PanelLayout();
        float w = ClientSize.Width, h = ClientSize.Height;
        var pad = ScF(12);

        layout.Inline = h < ScF(120);
        var headerH = layout.Inline ? ScF(40) : ScF(92);
        var statusH = layout.Inline ? 0f : ScF(18);

        layout.Header = new RectangleF(pad, ScF(6), Math.Max(0, w - pad * 2), headerH);
        layout.Status = new RectangleF(pad, h - statusH - ScF(2), Math.Max(0, w - pad * 2), statusH);

        var chartTop = layout.Header.Bottom + ScF(2);
        var chartBottom = h - statusH - ScF(6);
        var chart = new RectangleF(pad, chartTop, Math.Max(0, w - pad * 2), Math.Max(0, chartBottom - chartTop));
        layout.Chart = chart;

        layout.ShowChart = !layout.Inline && chart.Height >= ScF(70) && chart.Width >= ScF(150);
        if (!layout.ShowChart) return layout;

        var chipsH = ScF(22);
        var tickH = ScF(16);
        var priceW = ScF(48);

        layout.Chips = new RectangleF(chart.X, chart.Y, chart.Width, chipsH);
        layout.Plot = new RectangleF(
            chart.X,
            chart.Y + chipsH,
            Math.Max(10, chart.Width - priceW),
            Math.Max(10, chart.Height - chipsH - tickH));

        // 时间跨度跟随可视面积：窗口越大，回看得越久
        var area = layout.Plot.Width * layout.Plot.Height;
        var s2 = (float)DeviceDpi / 96f;
        s2 *= s2;

        layout.Hours = layout.Plot.Width < ScF(150) ? 1
            : area >= 150000 * s2 ? 24
            : area >= 90000 * s2 ? 12
            : area >= 48000 * s2 ? 6
            : area >= 22000 * s2 ? 3
            : 1;

        return layout;
    }

    private static string RangeText(double hours)
    {
        foreach (var stop in new[] { 1.0, 3, 6, 12, 24 })
            if (Math.Abs(hours - stop) <= stop * 0.15)
                return "近 " + stop.ToString("0") + " 小时";

        if (hours < 1) return "近 " + Math.Max(5, Math.Round(hours * 60)) + " 分钟";
        return "近 " + hours.ToString(hours >= 10 ? "0" : "0.#") + " 小时";
    }

    // ------------------------------------------------------------------ 绘制

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Bg);

        _hits.Clear();
        var layout = ComputeLayout();

        PaintHeader(g, layout);
        if (layout.ShowChart) PaintChart(g, layout);
        PaintStatus(g, layout);
    }

    private void PaintHeader(Graphics g, in PanelLayout layout)
    {
        var r = layout.Header;
        if (layout.Inline)
        {
            PaintInline(g, r);
            return;
        }

        PaintButtons(g, r);

        var reserved = ScF(4 * 30 + 6);
        var rowW = r.Width - reserved;
        if (rowW < ScF(150))
        {
            // 太窄时退回单行价格，避免被裁切
            PaintInline(g, r);
            return;
        }

        var rowH = r.Height / 2f;

        PaintPriceRow(g, new RectangleF(r.X, r.Y, rowW, rowH), _intl, "国际金价", "USD/oz", Theme.Gold, _fPrice, _errIntl);
        PaintPriceRow(g, new RectangleF(r.X, r.Y + rowH, rowW, rowH), _dom, "国内金价", "元/克", Theme.Cyan, _fPrice2, _errDom);
    }

    private void PaintPriceRow(Graphics g, RectangleF rect, Quote? quote, string label, string defaultUnit,
        Color accent, Font priceFont, string? error)
    {
        using var bAccent = new SolidBrush(accent);
        using var bMuted = new SolidBrush(Theme.Muted);
        using var bText = new SolidBrush(Theme.Text);

        // 左侧小色条 + 标签
        using (var bar = new SolidBrush(Color.FromArgb(120, accent)))
            g.FillRectangle(bar, rect.X, rect.Y + ScF(3), ScF(2.2f), ScF(9));
        g.DrawString(label, _fLabel, bMuted, rect.X + ScF(7), rect.Y + ScF(1));

        var price = quote != null ? Fmt.Price(quote.Price) : "—";
        var brush = quote != null ? bText : bMuted;
        var py = rect.Y + ScF(13);
        var size = g.MeasureString(price, priceFont);
        g.DrawString(price, priceFont, brush, rect.X, py);

        var unit = quote?.Unit ?? defaultUnit;
        var unitSize = g.MeasureString(unit, _fUnit);
        var ux = rect.X + size.Width - ScF(3);
        var uy = py + size.Height - unitSize.Height - ScF(5);
        g.DrawString(unit, _fUnit, bMuted, ux, uy);

        if (quote?.ChangePct is double pct)
        {
            using var bChange = new SolidBrush(pct >= 0 ? Theme.Up : Theme.Down);
            g.DrawString(Fmt.Pct(pct), _fChange, bChange, ux + unitSize.Width + ScF(7), uy - ScF(1));
        }
        else if (quote == null && error != null)
        {
            using var bErr = new SolidBrush(Theme.Red);
            g.DrawString("加载失败", _fSmall, bErr, ux + unitSize.Width + ScF(7), uy);
        }
    }

    private void PaintInline(Graphics g, RectangleF rect)
    {
        using var bGold = new SolidBrush(Theme.Gold);
        using var bCyan = new SolidBrush(Theme.Cyan);
        using var bMuted = new SolidBrush(Theme.Muted);

        var p1 = _intl != null ? Fmt.Price(_intl.Price) : "—";
        var p2 = _dom != null ? Fmt.Price(_dom.Price) : "—";

        var y = rect.Y + (rect.Height - _fInline.GetHeight(g)) / 2f;
        var x = rect.X + ScF(2);

        var s1 = g.MeasureString(p1, _fInline);
        g.DrawString(p1, _fInline, bGold, x, y);
        var u1 = g.MeasureString(" /oz", _fUnit);
        g.DrawString(" /oz", _fUnit, bMuted, x + s1.Width - ScF(3), y + s1.Height - u1.Height - ScF(2));

        x += s1.Width + u1.Width - ScF(1) + ScF(12);
        var s2 = g.MeasureString(p2, _fInline);
        if (x + s2.Width < rect.Right + ScF(20))
        {
            g.DrawString(p2, _fInline, bCyan, x, y);
            var u2 = g.MeasureString(" 元/克", _fUnit);
            g.DrawString(" 元/克", _fUnit, bMuted, x + s2.Width - ScF(3), y + s2.Height - u2.Height - ScF(2));
        }
    }

    private void PaintButtons(Graphics g, RectangleF header)
    {
        var bw = ScF(26);
        var bh = ScF(24);
        var gap = ScF(3);
        var y = header.Y + ScF(1);
        var x = header.Right - (bw + gap) * 4 + gap;

        DrawIconButton(g, new RectangleF(x, y, bw, bh), "\uE718", _cfg.TopMost ? Theme.Gold : Theme.Muted, "pin");
        DrawIconButton(g, new RectangleF(x + bw + gap, y, bw, bh), "\uE72C", Theme.Muted, "refresh");
        DrawIconButton(g, new RectangleF(x + (bw + gap) * 2, y, bw, bh), "\uE713", Theme.Muted, "settings");
        DrawIconButton(g, new RectangleF(x + (bw + gap) * 3, y, bw, bh), "\uE921", Theme.Muted, "hide");
    }

    private void DrawIconButton(Graphics g, RectangleF rect, string glyph, Color color, string action)
    {
        var hover = rect.Contains(_mouse);
        if (hover)
        {
            using var path = Theme.Round(rect, ScF(5));
            using var brush = new SolidBrush(Color.FromArgb(28, 255, 255, 255));
            g.FillPath(brush, path);
        }

        using var textBrush = new SolidBrush(color);
        var size = g.MeasureString(glyph, _fIcon);
        g.DrawString(glyph, _fIcon, textBrush,
            rect.X + (rect.Width - size.Width) / 2f,
            rect.Y + (rect.Height - size.Height) / 2f - ScF(1));

        _hits.Add(new HitArea(rect, action));
    }

    private void PaintChart(Graphics g, in PanelLayout layout)
    {
        var source = _chartKey == "dom" ? _histDom : _histIntl;
        var accent = _chartKey == "dom" ? Theme.Cyan : Theme.Gold;
        var plot = layout.Plot;
        var hours = layout.Hours;

        var now = DateTime.Now;
        var t0 = now.AddHours(-hours);
        var t1 = now;
        var points = new List<Sample>();

        if (source.Count > 0)
        {
            var lastTime = source[^1].T;
            t1 = (now - lastTime).TotalMinutes > 45 ? lastTime.AddMinutes(2) : now;
            t0 = t1.AddHours(-hours);
            points = source.Where(s => s.T >= t0 && s.T <= t1).ToList();

            if (points.Count < 2)
            {
                // 窗口内几乎没有数据（休市/刚启动）：退回最近可用的一段
                points = source.Where(s => (t1 - s.T).TotalHours <= Math.Min(26, hours * 6)).ToList();
                if (points.Count >= 2)
                {
                    t0 = points[0].T;
                    t1 = points[^1].T;
                }
            }
            else if ((points[^1].T - points[0].T).TotalHours < hours * 0.45)
            {
                // 数据只覆盖窗口一小段时，按实际覆盖范围重设横轴，避免只剩一根竖线
                var wide = source.Where(s => (t1 - s.T).TotalHours <= Math.Min(26, hours * 6)).ToList();
                if (wide.Count >= 2 && (wide[^1].T - wide[0].T).TotalHours > (points[^1].T - points[0].T).TotalHours)
                    points = wide;
                t0 = points[0].T;
                t1 = points[^1].T;
            }

            if (t1 <= t0) t1 = t0.AddMinutes(1);
        }

        var actualHours = Math.Max(0.1, (t1 - t0).TotalHours);
        DrawChips(g, layout, actualHours);

        if (points.Count < 2)
        {
            DrawCentered(g, source.Count == 0 ? "正在获取走势数据…" : "数据积累中，稍后显示走势",
                Theme.Muted, plot, _fSmall);
            return;
        }

        var min = points.Min(p => p.P);
        var max = points.Max(p => p.P);
        if (max - min < 1e-9)
        {
            min -= 0.5;
            max += 0.5;
        }
        var padY = (max - min) * 0.15;
        min -= padY;
        max += padY;

        var total = (t1 - t0).TotalSeconds;
        if (total <= 0) total = 1;

        float X(DateTime t) => plot.X + (float)((t - t0).TotalSeconds / total) * plot.Width;
        float Y(double v) => plot.Bottom - (float)((v - min) / (max - min)) * plot.Height;

        // 网格 + 价格刻度
        using (var gridPen = new Pen(Color.FromArgb(22, 255, 255, 255), 1f))
        using (var bMuted = new SolidBrush(Theme.Muted))
        {
            for (var i = 0; i <= 3; i++)
            {
                var y = plot.Y + plot.Height * i / 3f;
                g.DrawLine(gridPen, plot.X, y, plot.Right, y);

                var v = max - (max - min) * i / 3.0;
                var text = Fmt.Price(v, max - min < 5 ? 2 : max - min < 50 ? 1 : 0);
                var size = g.MeasureString(text, _fTick);
                g.DrawString(text, _fTick, bMuted, plot.Right + ScF(6), y - size.Height / 2f);
            }
        }

        // 面积渐变 + 折线（休市/数据缺失处断开，避免画出假的直线段）
        var segments = new List<List<Sample>>();
        var current = new List<Sample> { points[0] };
        for (var i = 1; i < points.Count; i++)
        {
            if ((points[i].T - points[i - 1].T).TotalMinutes > 15)
            {
                segments.Add(current);
                current = new List<Sample>();
            }
            current.Add(points[i]);
        }
        segments.Add(current);

        using (var gradient = new LinearGradientBrush(
                   new RectangleF(plot.X, plot.Y, Math.Max(1, plot.Width), Math.Max(1, plot.Height)),
                   Color.FromArgb(64, accent), Color.FromArgb(0, accent), LinearGradientMode.Vertical))
        using (var linePen = new Pen(accent, Math.Max(1.2f, ScF(1.6f)))
               {
                   LineJoin = LineJoin.Round,
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round,
               })
        {
            foreach (var segment in segments)
            {
                if (segment.Count < 2) continue;

                var pts = new PointF[segment.Count];
                for (var i = 0; i < segment.Count; i++)
                    pts[i] = new PointF(X(segment[i].T), Y(segment[i].P));

                using var fillPath = new GraphicsPath();
                fillPath.AddLines(pts);
                fillPath.AddLine(pts[^1].X, plot.Bottom, pts[0].X, plot.Bottom);
                fillPath.CloseFigure();
                g.FillPath(gradient, fillPath);

                using var linePath = new GraphicsPath();
                linePath.AddLines(pts);
                g.DrawPath(linePen, linePath);
            }
        }

        // 末端圆点
        var last = new PointF(X(points[^1].T), Y(points[^1].P));
        using (var glow = new SolidBrush(Color.FromArgb(45, accent)))
            g.FillEllipse(glow, last.X - ScF(6), last.Y - ScF(6), ScF(12), ScF(12));
        using (var dot = new SolidBrush(accent))
            g.FillEllipse(dot, last.X - ScF(3), last.Y - ScF(3), ScF(6), ScF(6));

        // 时间刻度
        using (var bMuted = new SolidBrush(Theme.Muted))
        {
            var labelY = plot.Bottom + ScF(2);
            var left = t0.ToString("HH:mm");
            var mid = t0.AddSeconds((t1 - t0).TotalSeconds / 2).ToString("HH:mm");
            var right = t1.ToString("HH:mm");

            g.DrawString(left, _fTick, bMuted, plot.X, labelY);
            var midSize = g.MeasureString(mid, _fTick);
            if (plot.Width > ScF(180))
                g.DrawString(mid, _fTick, bMuted, plot.X + (plot.Width - midSize.Width) / 2f, labelY);
            var rightSize = g.MeasureString(right, _fTick);
            g.DrawString(right, _fTick, bMuted, plot.Right - rightSize.Width, labelY);
        }

        // 悬停十字线
        if (_mouse.X >= plot.Left && _mouse.X <= plot.Right && _mouse.Y >= plot.Top && _mouse.Y <= plot.Bottom)
        {
            var nearest = points[0];
            var best = double.MaxValue;
            foreach (var p in points)
            {
                var d = Math.Abs(X(p.T) - _mouse.X);
                if (d < best)
                {
                    best = d;
                    nearest = p;
                }
            }

            var hx = X(nearest.T);
            var hy = Y(nearest.P);
            using (var crossPen = new Pen(Color.FromArgb(70, 255, 255, 255), 1f) { DashStyle = DashStyle.Dot })
                g.DrawLine(crossPen, hx, plot.Top, hx, plot.Bottom);
            using (var hb = new SolidBrush(accent))
                g.FillEllipse(hb, hx - ScF(2.5f), hy - ScF(2.5f), ScF(5), ScF(5));

            var text = nearest.T.ToString("HH:mm") + "   " + Fmt.Price(nearest.P);
            var size = g.MeasureString(text, _fSmall);
            var boxW = size.Width + ScF(14);
            var boxH = size.Height + ScF(8);
            var bx = Math.Min(Math.Max(hx + ScF(10), plot.X), plot.Right - boxW);
            var by = Math.Max(plot.Y, hy - boxH - ScF(10));

            using (var path = Theme.Round(new RectangleF(bx, by, boxW, boxH), ScF(4)))
            using (var bg = new SolidBrush(Color.FromArgb(238, 38, 42, 50)))
            using (var border = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }
            using (var tb = new SolidBrush(Theme.Text))
                g.DrawString(text, _fSmall, tb, bx + ScF(7), by + ScF(4));
        }
    }

    private void DrawChips(Graphics g, in PanelLayout layout, double hours)
    {
        var chips = new[] { ("intl", "国际"), ("dom", "国内") };
        var x = layout.Chips.X;
        var y = layout.Chips.Y + ScF(2);
        var h = ScF(18);

        foreach (var (key, label) in chips)
        {
            var size = g.MeasureString(label, _fChip);
            var w = size.Width + ScF(16);
            var rect = new RectangleF(x, y, w, h);
            var selected = _chartKey == key;
            var accent = key == "intl" ? Theme.Gold : Theme.Cyan;

            using (var path = Theme.Round(rect, ScF(9)))
            using (var brush = new SolidBrush(selected
                       ? Color.FromArgb(36, accent)
                       : Color.FromArgb(14, 255, 255, 255)))
            {
                g.FillPath(brush, path);
            }

            using var textBrush = new SolidBrush(selected ? accent : Theme.Muted);
            g.DrawString(label, _fChip, textBrush, rect.X + ScF(8), rect.Y + ScF(3));

            _hits.Add(new HitArea(rect, "series:" + key));
            x += w + ScF(6);
        }

        using var bMuted = new SolidBrush(Theme.Muted);
        var range = RangeText(hours);
        var rangeSize = g.MeasureString(range, _fTick);
        g.DrawString(range, _fTick, bMuted, layout.Chips.Right - rangeSize.Width, layout.Chips.Y + ScF(4));
    }

    private void PaintStatus(Graphics g, in PanelLayout layout)
    {
        if (layout.Status.Height <= 0) return;

        var text = BuildStatusText();
        _statusCache = text;

        var color = _busy ? Theme.Amber
            : (_errIntl != null || _errDom != null)
                ? ((_intl != null || _dom != null) ? Theme.Amber : Theme.Red)
                : ((_intl != null || _dom != null) ? Theme.Green : Theme.Muted);

        var r = layout.Status;
        var dotSize = ScF(5);
        var cy = r.Y + r.Height / 2f;

        if (_busy)
        {
            using var arcPen = new Pen(Theme.Amber, Math.Max(1.2f, ScF(1.3f))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var box = new RectangleF(r.X, cy - dotSize, dotSize * 2, dotSize * 2);
            g.DrawArc(arcPen, box, _spin, 250);
        }
        else
        {
            using var dot = new SolidBrush(color);
            g.FillEllipse(dot, r.X, cy - dotSize / 2f, dotSize, dotSize);
        }

        using var bMuted = new SolidBrush(Theme.Muted);
        var textRect = new RectangleF(r.X + dotSize + ScF(7), r.Y, Math.Max(10, r.Width - dotSize - ScF(7)), r.Height);
        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(text, _fSmall, bMuted, textRect, format);
    }

    private string BuildStatusText()
    {
        if (_busy) return "正在刷新行情…";

        var parts = new List<string>();
        var quote = _intl ?? _dom;
        if (quote != null) parts.Add("行情 " + quote.QuoteTime.ToString("HH:mm:ss"));
        var source = _intl?.Source ?? _dom?.Source;
        if (source != null) parts.Add(source);
        parts.Add("每 " + _cfg.RefreshSeconds + " 秒");

        if (_errIntl != null || _errDom != null)
        {
            parts.Add("⚠ " + (_errIntl ?? _errDom));
            var wait = (int)Math.Ceiling(Math.Max(0, (_nextFetchAt - DateTime.Now).TotalSeconds));
            parts.Add(wait + "s 后重试");
        }
        else if (quote == null && _lastAttempt == DateTime.MinValue)
        {
            return "正在获取行情…";
        }

        return string.Join("  ·  ", parts);
    }

    private static void DrawCentered(Graphics g, string text, Color color, RectangleF rect, Font font)
    {
        using var brush = new SolidBrush(color);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, rect, sf);
    }

    // ------------------------------------------------------------------ 交互

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;

        var hit = _hits.FirstOrDefault(h => h.Rect.Contains(e.Location));
        Cursor = hit.Action != null ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = new Point(-1, -1);
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;

        var hit = _hits.FirstOrDefault(h => h.Rect.Contains(e.Location));
        if (hit.Action == null) return;

        switch (hit.Action)
        {
            case "pin":
                ToggleTopMost();
                break;
            case "refresh":
                ForceRefresh();
                break;
            case "settings":
                OpenSettings();
                break;
            case "hide":
                HideToTray();
                break;
            default:
                if (hit.Action.StartsWith("series:", StringComparison.Ordinal))
                {
                    _chartKey = hit.Action[7..] == "dom" ? "dom" : "intl";
                    _cfg.ChartSeries = _chartKey;
                    _cfg.Save();
                    Invalidate();
                }
                break;
        }
    }

    private bool _suppressMinimize;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_suppressMinimize && WindowState == FormWindowState.Minimized)
        {
            _suppressMinimize = true;
            try { HideToTray(); }
            finally { _suppressMinimize = false; }
        }
        Invalidate();
    }

    private void OpenSettings()
    {
        if (_docked) ExpandFromMini(true);
        CancelTempExpand();   // 设置对话框是模态的，别在它打开时把窗口收回小窗

        using var dialog = new SettingsForm(_cfg, _hotkeyOk);

        // 主窗口是置顶窗口时，模态框也置顶，否则可能被别的窗口压住
        dialog.TopMost = TopMost;

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var next = dialog.Result;
        var sourceChanged = next.SourcePreference != _cfg.SourcePreference ||
                            next.IntlSymbol != _cfg.IntlSymbol ||
                            next.DomSymbol != _cfg.DomSymbol;

        _cfg = next;
        _cfg.Save();

        TopMost = _cfg.TopMost;
        _chartKey = _cfg.ChartSeries;
        RegisterHotkey();

        if (sourceChanged || dialog.RefreshRequested)
        {
            _failCount = 0;
            _nextFetchAt = DateTime.MinValue;
            _nextHistAt = DateTime.MinValue;
            _intl = null;
            _dom = null;
            _errIntl = null;
            _errDom = null;
        }

        if (!_hotkeyOk)
        {
            MessageBox.Show(this,
                "快捷键 “" + _cfg.Hotkey + "” 无法注册（格式错误或被其他程序占用）。\n" +
                "仍可通过托盘图标显示/隐藏窗口。",
                "快捷键不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Invalidate();
        _ = RefreshAsync();
    }

    private readonly record struct HitArea(RectangleF Rect, string? Action);
}
