using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GoldTicker;

public enum DockEdge { Left, Right }

/// <summary>
/// 贴边收起后的悬浮小窗：无边框、置顶、两行价格（上国际 / 下国内）。
/// 单击或悬停都是临时展开完整窗口（3 秒后失焦自动收回）；拖动可移动，拖离边缘则展开、拖到边缘则继续吸边。
/// </summary>
public sealed class MiniForm : Form
{
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public event Action? ExpandRequested;   // 单击 / 悬停都只是临时展开，失焦后由主窗自动收回
    public event Action<Point>? MovedByUser;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool HoverExpand { get; set; } = true;

    private readonly Font _fPrice;


    private Quote? _intl;
    private Quote? _dom;
    private bool _hover;
    private bool _pressed;
    private bool _dragging;
    private Point _downScreen;
    private Point _downLocation;
    private System.Windows.Forms.Timer? _hoverTimer;
    private readonly System.Windows.Forms.Timer _armTimer;
    private bool _hoverReady;
    private bool _hoverBlocked;

    private bool _hasDock;
    private DockEdge _edge;
    private Rectangle _workArea;
    private Rectangle _anchor;
    private int _gap = 6;

    public MiniForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Theme.Bg2;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        _fPrice = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);


        Size = new Size(Sc(116), Sc(64));
        Cursor = Cursors.Hand;

        // 刚出现时鼠标往往就停在卡片上（刚把窗口拖到边缘），先别急着展开
        _armTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        _armTimer.Tick += (_, _) =>
        {
            _armTimer.Stop();
            _hoverReady = true;
            if (ClientRectangle.Contains(PointToClient(Cursor.Position))) _hoverBlocked = true;
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW，无边框时也有一圈阴影
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitSize();
        ApplyDock();
        _armTimer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var round = DWMWCP_ROUND;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch
        {
            // 老系统没有圆角，忽略
        }
    }

    public void SetQuotes(Quote? intl, Quote? dom)
    {
        _intl = intl;
        _dom = dom;
        if (IsHandleCreated) Invalidate();
    }

    /// <summary>按屏幕边缘摆放：垂直方向对齐完整窗口中心，超出工作区则夹住。</summary>
    /// <summary>按屏幕边缘摆放；记住锚点，尺寸变化后可以重算。</summary>
    public void DockTo(Rectangle workArea, DockEdge edge, Rectangle anchor, int gap = 6)
    {
        _workArea = workArea;
        _edge = edge;
        _anchor = anchor;
        _gap = gap;
        _hasDock = true;
        ApplyDock();
    }

    private void ApplyDock()
    {
        if (!_hasDock) return;

        var g = Sc(_gap);
        var x = _edge == DockEdge.Left ? _workArea.Left + g : _workArea.Right - Width - g;
        var y = _anchor.Y + (_anchor.Height - Height) / 2;
        y = Math.Clamp(y, _workArea.Top + Sc(8), Math.Max(_workArea.Top + Sc(8), _workArea.Bottom - Height - Sc(8)));
        Location = new Point(x, y);
    }

    /// <summary>卡片宽度按当前数字长度贴合，避免空白。</summary>
    private void FitSize()
    {
        if (!IsHandleCreated) return;

        using var g = CreateGraphics();
        var w1 = g.MeasureString(Fmt.Price(_intl?.Price ?? 0), _fPrice).Width;
        var w2 = g.MeasureString(Fmt.Price(_dom?.Price ?? 0), _fPrice).Width;
        var w = (int)Math.Ceiling(Math.Max(w1, w2)) + Sc(28);
        Size = new Size(Math.Max(Sc(92), w), Sc(64));
    }


    private int Sc(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    // ------------------------------------------------------------------ 绘制

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var radius = Sc(9);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        using (var path = Theme.Round(rect, radius))
        {
            using var bg = new SolidBrush(_hover ? Color.FromArgb(36, 40, 48) : Theme.Bg2);
            g.FillPath(bg, path);
            using var border = new Pen(Color.FromArgb(_hover ? 90 : 50, 255, 255, 255), 1f);
            g.DrawPath(border, path);
        }

        var rowH = ClientSize.Height / 2f;
        PaintRow(g, new RectangleF(0, 0, Width, rowH), _intl, Theme.Gold);
        PaintRow(g, new RectangleF(0, rowH, Width, rowH), _dom, Theme.Cyan);

        using (var sep = new Pen(Color.FromArgb(16, 255, 255, 255), 1f))
            g.DrawLine(sep, Sc(10), rowH, Width - Sc(10), rowH);
    }

    /// <summary>只画数字，右对齐。</summary>
    private void PaintRow(Graphics g, RectangleF row, Quote? quote, Color accent)
    {
        using var bMuted = new SolidBrush(Theme.Muted);
        using var bAccent = new SolidBrush(accent);

        var text = quote != null ? Fmt.Price(quote.Price) : "—";
        var size = g.MeasureString(text, _fPrice);
        var x = Math.Max(Sc(8), row.Right - Sc(13) - size.Width);

        g.DrawString(text, _fPrice, quote != null ? bAccent : bMuted,
            x, row.Y + (row.Height - size.Height) / 2f);
    }

    // ------------------------------------------------------------------ 交互

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();

        if (!HoverExpand || !_hoverReady || _hoverBlocked) return;
        _hoverTimer ??= new System.Windows.Forms.Timer { Interval = 700 };
        _hoverTimer.Tick -= OnHoverTick;
        _hoverTimer.Tick += OnHoverTick;
        _hoverTimer.Start();
    }

    private void OnHoverTick(object? sender, EventArgs e)
    {
        _hoverTimer?.Stop();
        if (!IsHandleCreated || IsDisposed) return;
        if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) return;
        ExpandRequested?.Invoke();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _hoverBlocked = false;
        _hoverTimer?.Stop();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        _dragging = false;
        _hoverTimer?.Stop();
        _hoverBlocked = true;
        _downScreen = Cursor.Position;
        _downLocation = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed) return;

        var dx = Cursor.Position.X - _downScreen.X;
        var dy = Cursor.Position.Y - _downScreen.Y;
        if (!_dragging && (Math.Abs(dx) > Sc(3) || Math.Abs(dy) > Sc(3))) _dragging = true;
        if (_dragging) Location = new Point(_downLocation.X + dx, _downLocation.Y + dy);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_pressed) return;

        _pressed = false;
        Capture = false;
        _hoverTimer?.Stop();

        if (_dragging) MovedByUser?.Invoke(Location);
        else ExpandRequested?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _armTimer.Stop();
            _armTimer.Dispose();
            _hoverTimer?.Stop();
            _hoverTimer?.Dispose();
            _fPrice.Dispose();
        }
        base.Dispose(disposing);
    }
}
