using System.Net;
using System.Text;
using System.Text.Json;
using JevBrowse.App.Renderer;
using JevBrowse.Domain;

namespace JevBrowse.App;

public sealed partial class MainWindow
{
    /// <summary>Real engine: Back and Forward keep working after a tab sleeps and wakes (a new renderer that starts with one history entry).</summary>
    private async Task RunHistoryCheckAsync()
    {
        _tick?.Stop();
        var k = _kernel!;
        var steps = new List<object>(); var pass = true;
        void Step(string name, bool ok, string detail = "") { steps.Add(new { name, ok, detail }); if (!ok) pass = false; }
        async Task<bool> Until(Func<bool> cond, int ms = 10000) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(100); } return cond(); }
        WebView2Lease Lease(VirtualTab t) => (WebView2Lease)(_leases!.TryGet(t.Id, out var l) ? l : throw new InvalidOperationException("no renderer"));

        var listener = new HttpListener(); var port = 47940;
        for (; port < 47960; port++) { listener.Prefixes.Clear(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); try { listener.Start(); break; } catch (Exception) { } }
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext c; try { c = await listener.GetContextAsync(); } catch (Exception) { break; }
                var bytes = Encoding.UTF8.GetBytes($"<!doctype html><title>{c.Request.Url!.AbsolutePath}</title><p>{c.Request.Url.AbsolutePath}</p>");
                c.Response.ContentType = "text/html; charset=utf-8"; c.Response.ContentLength64 = bytes.Length;
                await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
            }
        });
        string U(string p) => $"http://127.0.0.1:{port}{p}";
        async Task<bool> At(VirtualTab t, string path, int ms = 12000) => await Until(() => _leases!.TryGet(t.Id, out var l) && ((WebView2Lease)l).View.CoreWebView2.Source == U(path), ms);
        async Task Go(VirtualTab t, string path) { Lease(t).Navigate(new Uri(U(path))); await At(t, path); await Task.Delay(400); }

        try
        {
            var parked = k.Open(new Uri(U("/parked")));
            await k.ActivateAsync(parked.Id);
            var t = k.Open(new Uri(U("/p1")));
            await k.ActivateAsync(t.Id);
            await At(t, "/p1");
            foreach (var p in new[] { "/p2", "/p3", "/p4" }) await Go(t, p);
            Lease(t).View.GoBack();                                   // showing p3, with p4 ahead
            await At(t, "/p3"); await Task.Delay(500);

            async Task SleepAndWake()
            {
                await k.ActivateAsync(parked.Id);
                var r = await k.VirtualizeAsync(t.Id, Cause.User);
                if (!r.Allowed) throw new InvalidOperationException("could not sleep: " + r.Reason);
                await k.ActivateAsync(t.Id);
            }

            await SleepAndWake();
            Step("the tab wakes on the page it slept on", await At(t, "/p3"), Lease(t).View.CoreWebView2.Source);
            var l1 = Lease(t);
            Step("the fresh renderer offers Back and Forward again", l1.CanGoBackAcrossSleep && l1.CanGoForwardAcrossSleep, $"back={l1.CanGoBackAcrossSleep} forward={l1.CanGoForwardAcrossSleep}");

            l1.GoBackAcrossSleep(); Step("Back goes to the page before it", await At(t, "/p2"), Lease(t).View.CoreWebView2.Source);
            Lease(t).GoBackAcrossSleep(); Step("…and Back again to the first page", await At(t, "/p1"), Lease(t).View.CoreWebView2.Source);
            await Task.Delay(500);
            Step("…and there Back is unavailable (the real start)", !Lease(t).CanGoBackAcrossSleep);
            Lease(t).GoForwardAcrossSleep(); Step("Forward walks to /p2", await At(t, "/p2"), Lease(t).View.CoreWebView2.Source);
            await Task.Delay(500); Lease(t).GoForwardAcrossSleep(); Step("…then /p3", await At(t, "/p3"), Lease(t).View.CoreWebView2.Source);
            await Task.Delay(500); Lease(t).GoForwardAcrossSleep(); Step("…then /p4 (the page that was ahead when it slept)", await At(t, "/p4"), Lease(t).View.CoreWebView2.Source);
            await Task.Delay(500);
            Step("…and there Forward is unavailable", !Lease(t).CanGoForwardAcrossSleep);

            // a new navigation ends Forward but Back still reaches the whole past
            Lease(t).GoBackAcrossSleep(); await At(t, "/p3"); await Task.Delay(500);
            Lease(t).Navigate(new Uri(U("/p9"))); await At(t, "/p9"); await Task.Delay(600);
            Step("a new navigation ends Forward", !Lease(t).CanGoForwardAcrossSleep);
            await SleepAndWake();                                    // and it survives a second sleep
            Step("a second sleep and wake keeps Back", await At(t, "/p9") && Lease(t).CanGoBackAcrossSleep);
            Lease(t).GoBackAcrossSleep(); Step("Back from /p9 returns to /p3", await At(t, "/p3"), Lease(t).View.CoreWebView2.Source);
            await Task.Delay(500); Lease(t).GoBackAcrossSleep(); Step("…then /p2 (older pages still there)", await At(t, "/p2"), Lease(t).View.CoreWebView2.Source);
        }
        catch (Exception ex) { Step("no exception", false, ex.ToString()); }
        finally { try { listener.Stop(); listener.Close(); } catch (Exception) { } }

        var result = new { pass, steps, scope = "Real WebView2; sleep and wake through the kernel; Back and Forward through the same lease methods the buttons use." };
        await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "history-check.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
