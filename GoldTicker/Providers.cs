using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GoldTicker;

public enum ErrKind { Timeout, Network, Http, Parse }

public sealed class FetchException : Exception
{
    public ErrKind Kind { get; }
    public FetchException(ErrKind kind, string message) : base(message) => Kind = kind;
}

/// <summary>
/// 行情抓取。国际金价：新浪 → 腾讯 → gold-api；国内金价：上期所黄金连续 → 黄金 T+D。
/// 所有请求都带超时，失败抛 <see cref="FetchException"/>，由界面决定降级展示。
/// </summary>
public sealed class QuoteFetcher
{
    private const string SinaReferer = "https://finance.sina.com.cn";
    private const int RequestTimeoutSeconds = 7;

    private static readonly HttpClient Http;

    static QuoteFetcher()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
        };
        Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        Http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
    }

    // ------------------------------------------------------------------ 报价

    public async Task<Quote> FetchIntlAsync(AppConfig cfg, CancellationToken ct)
    {
        var order = cfg.SourcePreference switch
        {
            "tencent" => new[] { "tencent", "sina", "goldapi" },
            "goldapi" => new[] { "goldapi", "sina", "tencent" },
            "sina" => new[] { "sina", "tencent", "goldapi" },
            _ => new[] { "sina", "tencent", "goldapi" },
        };

        FetchException? last = null;
        foreach (var source in order)
        {
            try
            {
                return source switch
                {
                    "tencent" => await FetchTencentIntlAsync(cfg, ct).ConfigureAwait(false),
                    "goldapi" => await FetchGoldApiAsync(ct).ConfigureAwait(false),
                    _ => await FetchSinaIntlAsync(cfg, ct).ConfigureAwait(false),
                };
            }
            catch (FetchException ex)
            {
                last = ex;
            }
            catch (Exception ex)
            {
                last = new FetchException(ErrKind.Network, Describe(ex));
            }
        }

        throw last ?? new FetchException(ErrKind.Network, "无可用国际金价数据源");
    }

    public async Task<Quote> FetchDomAsync(AppConfig cfg, CancellationToken ct)
    {
        var symbol = string.IsNullOrWhiteSpace(cfg.DomSymbol) ? "nf_AU0" : cfg.DomSymbol.Trim();
        try
        {
            return await FetchSinaDomesticAsync(symbol, ct).ConfigureAwait(false);
        }
        catch (FetchException first)
        {
            if (!symbol.StartsWith("nf_", StringComparison.OrdinalIgnoreCase)) throw;
            // AU0 挂掉时退到黄金 T+D（同为元/克）
            try
            {
                return await FetchSinaDomesticAsync("gds_AUTD", ct).ConfigureAwait(false);
            }
            catch
            {
                throw first;
            }
        }
    }

    private async Task<Quote> FetchSinaIntlAsync(AppConfig cfg, CancellationToken ct)
    {
        var symbol = string.IsNullOrWhiteSpace(cfg.IntlSymbol) ? "hf_XAU" : cfg.IntlSymbol.Trim();
        var body = await GetAsync($"https://hq.sinajs.cn/list={symbol}", SinaReferer, ct).ConfigureAwait(false);
        var payload = ExtractPayload(body) ?? throw new FetchException(ErrKind.Parse, "行情内容为空");
        var f = payload.Split(',');

        if (f.Length < 9) throw new FetchException(ErrKind.Parse, "行情字段异常");
        var price = Num(f[0]);
        var prev = Num(f[7]);
        if (price <= 0) throw new FetchException(ErrKind.Parse, "价格无效");

        return new Quote
        {
            Key = "intl",
            Label = "伦敦金",
            Price = price,
            PrevClose = prev,
            Unit = "USD/oz",
            Source = "新浪",
            QuoteTime = Combine(f.Length > 12 ? f[12] : "", f[6]),
        };
    }

    private async Task<Quote> FetchTencentIntlAsync(AppConfig cfg, CancellationToken ct)
    {
        var symbol = string.IsNullOrWhiteSpace(cfg.IntlSymbol) ? "hf_XAU" : cfg.IntlSymbol.Trim();
        var body = await GetAsync($"https://qt.gtimg.cn/q={symbol}", "https://gu.qq.com", ct).ConfigureAwait(false);
        var payload = ExtractPayload(body) ?? throw new FetchException(ErrKind.Parse, "行情内容为空");
        var f = payload.Split(',');

        if (f.Length < 9) throw new FetchException(ErrKind.Parse, "行情字段异常");
        var price = Num(f[0]);
        var prev = Num(f[7]);
        if (price <= 0) throw new FetchException(ErrKind.Parse, "价格无效");

        return new Quote
        {
            Key = "intl",
            Label = "伦敦金",
            Price = price,
            PrevClose = prev,
            Unit = "USD/oz",
            Source = "腾讯",
            QuoteTime = Combine(f.Length > 12 ? f[12] : "", f[6]),
        };
    }

    private async Task<Quote> FetchGoldApiAsync(CancellationToken ct)
    {
        var body = await GetAsync("https://api.gold-api.com/price/XAU", null, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("price", out var pe) || !pe.TryGetDouble(out var price) || price <= 0)
            throw new FetchException(ErrKind.Parse, "gold-api 返回异常");

        var ts = DateTime.Now;
        if (root.TryGetProperty("updatedAt", out var ue) && ue.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(ue.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
        {
            ts = dto.ToLocalTime().DateTime;
        }

        return new Quote
        {
            Key = "intl",
            Label = "现货黄金",
            Price = price,
            PrevClose = 0,
            Unit = "USD/oz",
            Source = "GoldAPI",
            QuoteTime = ts,
        };
    }

    private async Task<Quote> FetchSinaDomesticAsync(string symbol, CancellationToken ct)
    {
        var body = await GetAsync($"https://hq.sinajs.cn/list={symbol}", SinaReferer, ct).ConfigureAwait(false);
        var payload = ExtractPayload(body) ?? throw new FetchException(ErrKind.Parse, "行情内容为空");
        var f = payload.Split(',');

        if (symbol.StartsWith("nf_", StringComparison.OrdinalIgnoreCase))
        {
            // 上期所：名称,时间,开,高,低,昨收,买,卖,最新,结算,昨结算,...,日期
            if (f.Length < 18) throw new FetchException(ErrKind.Parse, "行情字段异常");
            var price = Num(f[8]);
            if (price <= 0) price = Num(f[7]);
            if (price <= 0) throw new FetchException(ErrKind.Parse, "价格无效");

            var prevClose = Num(f[5]);
            var prevSettle = Num(f[10]);
            return new Quote
            {
                Key = "dom",
                Label = "黄金连续",
                Price = price,
                PrevClose = prevClose > 0 ? prevClose : prevSettle,
                Unit = "元/克",
                Source = "新浪",
                QuoteTime = Combine(f[17], Hhmmss(f[1])),
            };
        }

        // 上海黄金交易所延期：最新,涨跌,买,卖,高,低,时间,昨收,开,...,日期
        if (f.Length < 13) throw new FetchException(ErrKind.Parse, "行情字段异常");
        var p = Num(f[0]);
        if (p <= 0) throw new FetchException(ErrKind.Parse, "价格无效");
        return new Quote
        {
            Key = "dom",
            Label = "黄金T+D",
            Price = p,
            PrevClose = Num(f[7]),
            Unit = "元/克",
            Source = "新浪·T+D",
            QuoteTime = Combine(f[12], f[6]),
        };
    }

    // ------------------------------------------------------------------ 历史（分钟线）

    public async Task<List<Sample>> FetchIntlHistoryAsync(string symbol, CancellationToken ct)
    {
        var code = StripPrefix(symbol, "XAU");
        var url = "https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20_=/" +
                  "GlobalFuturesService.getGlobalFuturesMinLine?symbol=" + Uri.EscapeDataString(code);
        var body = await GetAsync(url, SinaReferer, ct).ConfigureAwait(false);
        var json = ExtractJsonp(body, '{', '}') ?? throw new FetchException(ErrKind.Parse, "历史数据为空 len=" + body.Length);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("minLine_1d", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new FetchException(ErrKind.Parse,
                "历史结构异常 kind=" + doc.RootElement.ValueKind + " len=" + json.Length +
                " head=" + json.Substring(0, Math.Min(50, json.Length)).Replace("\n", " "));

        var list = new List<Sample>(1024);
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array) continue;
            var n = row.GetArrayLength();
            double price = 0;
            string? ts = null;

            // 首行是 [日期, 开, 交易所, 空, 时分, 价格, 量, 量, 均价, 完整时间]
            if (n >= 10) { price = NumEl(row[5]); ts = StrEl(row[9]); }
            else if (n >= 6) { price = NumEl(row[1]); ts = StrEl(row[5]); }

            if (price <= 0) continue;
            if (!DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
            list.Add(new Sample { T = t, P = price });
        }

        return list;
    }

    public async Task<List<Sample>> FetchDomHistoryAsync(string symbol, CancellationToken ct)
    {
        var code = StripPrefix(symbol, "AU0");
        var url = "https://stock2.finance.sina.com.cn/futures/api/jsonp.php/var%20_=/" +
                  "InnerFuturesNewService.getFewMinLine?symbol=" + Uri.EscapeDataString(code) + "&type=1";
        var body = await GetAsync(url, SinaReferer, ct).ConfigureAwait(false);
        var json = ExtractJsonp(body, '[', ']') ?? throw new FetchException(ErrKind.Parse, "历史数据为空 len=" + body.Length);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new FetchException(ErrKind.Parse, "历史结构异常 kind=" + doc.RootElement.ValueKind + " len=" + json.Length + " head=" + json.Substring(0, Math.Min(50, json.Length)).Replace("\n", " "));

        var list = new List<Sample>(2048);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty("c", out var ce) || !row.TryGetProperty("d", out var de)) continue;
            var price = NumEl(ce);
            if (price <= 0) continue;
            if (!DateTime.TryParse(StrEl(de), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
            list.Add(new Sample { T = t, P = price });
        }

        return list;
    }

    // ------------------------------------------------------------------ HTTP / 解析

    private static async Task<string> GetAsync(string url, string? referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(referer)) req.Headers.Referrer = new Uri(referer);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));

        try
        {
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new FetchException(ErrKind.Http, "接口返回 HTTP " + (int)resp.StatusCode);

            using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cts.Token).ConfigureAwait(false);
            return Encoding.Latin1.GetString(ms.ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FetchException(ErrKind.Timeout, "接口超时（" + RequestTimeoutSeconds + " 秒）");
        }
        catch (FetchException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FetchException(ErrKind.Network, Describe(ex));
        }
    }

    private static string Describe(Exception ex)
    {
        var msg = ex.GetBaseException().Message;
        if (msg.Length > 60) msg = msg.Substring(0, 60) + "…";
        return "网络错误：" + msg;
    }

    /// <summary>新浪行情串形如 var hq_str_hf_XAU="..."; 取引号内内容。</summary>
    private static string? ExtractPayload(string body)
    {
        var a = body.IndexOf('"');
        var b = body.LastIndexOf('"');
        if (a < 0 || b <= a + 1) return null;
        return body.Substring(a + 1, b - a - 1);
    }

    /// <summary>JSONP：取第一对括号内的 JSON。</summary>
    private static string? ExtractJsonp(string body, char open, char close)
    {
        var a = body.IndexOf(open);
        var b = body.LastIndexOf(close);
        if (a < 0 || b <= a) return null;
        return body.Substring(a, b - a + 1);
    }

    private static string StripPrefix(string symbol, string fallback)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return fallback;
        var i = symbol.IndexOf('_');
        return i >= 0 && i < symbol.Length - 1 ? symbol[(i + 1)..] : symbol;
    }

    private static double Num(string? s) =>
        double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double NumEl(JsonElement e) =>
        e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetDouble(out var d) ? d : 0,
            JsonValueKind.String => Num(e.GetString()),
            _ => 0,
        };

    private static string? StrEl(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    /// <summary>"111839" → "11:18:39"</summary>
    private static string Hhmmss(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 6 && s.All(char.IsDigit))
            return s.Substring(0, 2) + ":" + s.Substring(2, 2) + ":" + s.Substring(4, 2);
        return "";
    }

    private static DateTime Combine(string date, string time)
    {
        var text = (date ?? "").Trim() + " " + (time ?? "").Trim();
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return t;
        return DateTime.Now;
    }
}
