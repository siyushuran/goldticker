using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GoldTicker;

/// <summary>一个走势采样点（分钟级）。</summary>
public sealed class Sample
{
    public DateTime T { get; set; }
    public double P { get; set; }
}

/// <summary>一次成功抓取到的报价。</summary>
public sealed class Quote
{
    /// <summary>intl / dom</summary>
    public string Key { get; init; } = "intl";
    public string Label { get; init; } = "";
    public double Price { get; init; }
    public double PrevClose { get; init; }
    /// <summary>展示用单位/币种，如 "USD/oz"、"元/克"。</summary>
    public string Unit { get; init; } = "USD/oz";
    /// <summary>行情自带的时间戳（本地时间）。</summary>
    public DateTime QuoteTime { get; init; }
    public string Source { get; init; } = "";

    public double? ChangePct => PrevClose > 0 ? (Price - PrevClose) / PrevClose * 100.0 : null;
}

/// <summary>配置 + 走势缓存落盘位置。</summary>
public static class HistoryStore
{
    private static string? _dir;

    /// <summary>配置/缓存目录，自动挑选第一个可写位置（%APPDATA% → %LOCALAPPDATA% → 程序目录）。</summary>
    public static string Dir => _dir ??= ResolveDir();

    private static string ResolveDir()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoldTicker"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoldTicker"),
            Path.Combine(AppContext.BaseDirectory, "GoldTickerData"),
        };

        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return dir;
            }
            catch
            {
                // 换下一个候选目录
            }
        }

        return candidates[0];
    }

    public static string ConfigPath => Path.Combine(Dir, "config.json");

    private static string FileFor(string key) => Path.Combine(Dir, "history-" + key + ".json");

    public static List<Sample> Load(string key)
    {
        try
        {
            var p = FileFor(key);
            if (!File.Exists(p)) return new List<Sample>();
            var list = JsonSerializer.Deserialize<List<Sample>>(File.ReadAllText(p)) ?? new List<Sample>();
            var cut = DateTime.Now.AddHours(-30);
            return list.Where(s => s.T >= cut && s.P > 0).OrderBy(s => s.T).ToList();
        }
        catch
        {
            return new List<Sample>();
        }
    }

    public static void Save(string key, IReadOnlyList<Sample> samples)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(samples.OrderBy(s => s.T).ToList());
            var path = FileFor(key);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
        }
        catch
        {
            // 缓存写失败不影响主流程
        }
    }
}

public sealed class AppConfig
{
    /// <summary>刷新间隔（秒）。</summary>
    public int RefreshSeconds { get; set; } = 15;

    /// <summary>auto / sina / tencent / goldapi</summary>
    public string SourcePreference { get; set; } = "auto";

    public string IntlSymbol { get; set; } = "hf_XAU";
    public string DomSymbol { get; set; } = "nf_AU0";

    /// <summary>全局快捷键，如 Ctrl+Alt+G。</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+G";

    public bool TopMost { get; set; } = true;
    public bool StartHidden { get; set; }

    /// <summary>拖到屏幕左右边缘时自动收起为悬浮小窗。</summary>
    public bool EdgeAutoHide { get; set; } = true;

    /// <summary>鼠标悬停悬浮小窗时自动展开完整窗口。</summary>
    public bool MiniHoverExpand { get; set; } = true;

    /// <summary>判定"靠边"的距离（逻辑像素）。</summary>
    public int EdgeSnapDistance { get; set; } = 16;

    /// <summary>intl / dom</summary>
    public string ChartSeries { get; set; } = "intl";

    public int WinX { get; set; } = int.MinValue;
    public int WinY { get; set; } = int.MinValue;
    public int WinW { get; set; } = 420;
    public int WinH { get; set; } = 340;

    private static readonly JsonSerializerOptions JsonOpt = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(HistoryStore.ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(HistoryStore.ConfigPath), JsonOpt);
                if (cfg != null) return cfg.Normalized();
            }
        }
        catch
        {
            // 配置损坏时回落到默认值
        }
        return new AppConfig();
    }

    private AppConfig Normalized()
    {
        RefreshSeconds = Math.Clamp(RefreshSeconds, 3, 600);
        if (string.IsNullOrWhiteSpace(IntlSymbol)) IntlSymbol = "hf_XAU";
        if (string.IsNullOrWhiteSpace(DomSymbol)) DomSymbol = "nf_AU0";
        if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = "Ctrl+Alt+G";
        if (ChartSeries != "dom") ChartSeries = "intl";
        if (SourcePreference is not ("sina" or "tencent" or "goldapi")) SourcePreference = "auto";
        return this;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(HistoryStore.Dir);
            File.WriteAllText(HistoryStore.ConfigPath, JsonSerializer.Serialize(this, JsonOpt));
        }
        catch
        {
            // 忽略：多为此目录只读，不影响运行
        }
    }

    public AppConfig Clone() => (AppConfig)MemberwiseClone();
}

public static class Fmt
{
    public static string Price(double v, int decimals = 2) =>
        v.ToString("#,##0." + new string('0', Math.Max(0, decimals)), CultureInfo.InvariantCulture);

    public static string Pct(double? v) =>
        v is null ? "" : (v >= 0 ? "+" : "−") + Math.Abs(v.Value).ToString("0.00", CultureInfo.InvariantCulture) + "%";
}

/// <summary>极简滚动日志，仅用于排查抓取失败，超过 256KB 自动重建。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string Path => Resolve();

    private static string Resolve()
    {
        if (_path != null) return _path;

        var candidates = new List<string>
        {
            System.IO.Path.Combine(HistoryStore.Dir, "app.log"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "app.log"),
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "goldticker.log"),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(candidate, "");
                _path = candidate;
                return candidate;
            }
            catch
            {
                // 换下一个候选位置
            }
        }

        _path = candidates[2];
        return _path;
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Resolve();
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
