using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GoldTicker;

internal static class Program
{
    private const string MutexName = "Global\\GoldTicker.SingleInstance.7C1F";
    private const string ShowEventName = "Global\\GoldTicker.ShowWindow.7C1F";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
        {
            RunSelfTest();
            return;
        }

        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已经有实例在跑：让它把窗口叫到前台，本进程直接退出。
            // 这样「再点一次图标」永远能把托盘里的窗口找回来，而不是毫无反应。
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var signal))
                {
                    signal.Set();
                    signal.Dispose();
                }
            }
            catch
            {
                // 拿不到就安静退出
            }
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write("FATAL " + (e.ExceptionObject as Exception)?.Message);
        Application.ThreadException += (_, e) => Log.Write("UI EX " + e.Exception.Message);

        Log.Write("start, data dir = " + HistoryStore.Dir);

        var form = new MainForm();

        EventWaitHandle? showSignal = null;
        try
        {
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }
        catch
        {
            // 事件句柄建不出来也不影响主功能
        }

        if (showSignal != null)
        {
            var signal = showSignal;
            var listener = new Thread(() =>
            {
                while (true)
                {
                    signal.WaitOne();
                    try { form.BeginInvoke(new Action(form.ShowFromOutside)); }
                    catch { /* 窗口已销毁 */ }
                }
            })
            {
                IsBackground = true,
                Name = "show-signal",
            };
            listener.Start();
        }

        Application.Run(form);

        showSignal?.Dispose();
        GC.KeepAlive(mutex);
    }

    /// <summary>命令行自检：不走界面，直接抓一轮行情并把结果写到 selftest.txt。</summary>
    private static void RunSelfTest()
    {
        var sb = new StringBuilder();
        var cfg = new AppConfig();
        var fetcher = new QuoteFetcher();

        try
        {
            var q = fetcher.FetchIntlAsync(cfg, CancellationToken.None).GetAwaiter().GetResult();
            sb.AppendLine($"intl   OK  price={q.Price} unit={q.Unit} src={q.Source} time={q.QuoteTime:yyyy-MM-dd HH:mm:ss} pct={Fmt.Pct(q.ChangePct)}");
        }
        catch (Exception ex) { sb.AppendLine("intl   ERR " + ex.Message); }

        try
        {
            var q = fetcher.FetchDomAsync(cfg, CancellationToken.None).GetAwaiter().GetResult();
            sb.AppendLine($"dom    OK  price={q.Price} unit={q.Unit} src={q.Source} time={q.QuoteTime:yyyy-MM-dd HH:mm:ss} pct={Fmt.Pct(q.ChangePct)}");
        }
        catch (Exception ex) { sb.AppendLine("dom    ERR " + ex.Message); }

        try
        {
            var list = fetcher.FetchIntlHistoryAsync(cfg.IntlSymbol, CancellationToken.None).GetAwaiter().GetResult();
            sb.AppendLine($"hist-i {list.Count} pts  {list.FirstOrDefault()?.T:yyyy-MM-dd HH:mm} → {list.LastOrDefault()?.T:yyyy-MM-dd HH:mm}");
        }
        catch (Exception ex) { sb.AppendLine("hist-i ERR " + ex.Message); }

        try
        {
            var list = fetcher.FetchDomHistoryAsync(cfg.DomSymbol, CancellationToken.None).GetAwaiter().GetResult();
            sb.AppendLine($"hist-d {list.Count} pts  {list.FirstOrDefault()?.T:yyyy-MM-dd HH:mm} → {list.LastOrDefault()?.T:yyyy-MM-dd HH:mm}");
        }
        catch (Exception ex) { sb.AppendLine("hist-d ERR " + ex.Message); }

        sb.AppendLine("data dir = " + HistoryStore.Dir);
        sb.AppendLine("now      = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var outPath = Path.Combine(AppContext.BaseDirectory, "selftest.txt");
        File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
    }
}
