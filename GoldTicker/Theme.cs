using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GoldTicker;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(21, 23, 27);
    public static readonly Color Bg2 = Color.FromArgb(30, 33, 40);
    public static readonly Color Text = Color.FromArgb(232, 234, 237);
    public static readonly Color Muted = Color.FromArgb(140, 149, 162);
    public static readonly Color Gold = Color.FromArgb(232, 184, 75);
    public static readonly Color Cyan = Color.FromArgb(96, 190, 205);
    public static readonly Color Up = Color.FromArgb(232, 92, 74);      // 红涨
    public static readonly Color Down = Color.FromArgb(63, 174, 122);   // 绿跌
    public static readonly Color Green = Color.FromArgb(76, 190, 130);
    public static readonly Color Amber = Color.FromArgb(230, 176, 74);
    public static readonly Color Red = Color.FromArgb(226, 92, 78);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2f;
        if (d <= 0.5f || r.Width <= 0 || r.Height <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        d = Math.Min(d, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    /// <summary>把标题栏涂成和内容区一致（Win10 1809+ / Win11），失败则忽略。</summary>
    public static void Apply(IntPtr handle, Color background, Color foreground)
    {
        try
        {
            var one = 1;
            if (DwmSetWindowAttribute(handle, 20, ref one, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref one, sizeof(int));

            var caption = ColorRef(background);
            var text = ColorRef(foreground);
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));  // DWMWA_CAPTION_COLOR
            DwmSetWindowAttribute(handle, 34, ref caption, sizeof(int));  // DWMWA_BORDER_COLOR
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));     // DWMWA_TEXT_COLOR
        }
        catch
        {
            // 老系统不支持，保持系统默认配色
        }
    }
}

internal static class AppIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Create(int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var r = new RectangleF(size * 0.07f, size * 0.07f, size * 0.86f, size * 0.86f);
            using (var brush = new LinearGradientBrush(r, Color.FromArgb(255, 216, 128), Color.FromArgb(190, 136, 36), 55f))
                g.FillEllipse(brush, r);
            using (var pen = new Pen(Color.FromArgb(120, 82, 16), Math.Max(1f, size / 20f)))
                g.DrawEllipse(pen, r);

            using var font = new Font("Segoe UI", size * 0.40f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.FromArgb(60, 42, 10));
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("Au", font, textBrush, new RectangleF(0, size * 0.02f, size, size), sf);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}

internal static class HotkeyParser
{
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

    /// <summary>解析 "Ctrl+Alt+G" 这类写法。返回 false 表示格式非法。</summary>
    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    return false;
            }
        }

        if (modifiers == 0) return false;

        var key = parts[^1];
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                vk = c;
                return true;
            }
            return false;
        }

        if (key.Length is 2 or 3 && (key[0] == 'F' || key[0] == 'f') &&
            int.TryParse(key[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        return false;
    }
}
