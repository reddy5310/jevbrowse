using System.Net;
using System.Text;
using System.Text.Json;
using JevBrowse.AgentGateway;
using JevBrowse.App.Renderer;
using JevBrowse.Domain;

namespace JevBrowse.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// Real engine, real address bar: what the bar shows after a followed link, a redirect, Back and Forward, while the person is typing, and while an agent
    /// navigates in the background; that a new-window request never opens an unmanaged window (blocked without a gesture, a managed tab in the same workspace with
    /// one); and that a dead browser process is replaced instead of reused. Trusted input events come from the DevTools protocol, so "the person clicked" is real.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private string DescribeWindows()
    {
        var mine = new HashSet<uint> { (uint)Environment.ProcessId }; foreach (var p in _leases!.ProcessIds) mine.Add((uint)p);
        var list = new List<string>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (mine.Contains(pid) && IsWindowVisible(h)) { var c = new System.Text.StringBuilder(256); var t = new System.Text.StringBuilder(256); GetClassName(h, c, 256); GetWindowText(h, t, 256); GetWindowRect(h, out var rc); list.Add($"{c}|{t}|{rc.L},{rc.T} {rc.R - rc.L}x{rc.B - rc.T}|owner={(GetWindow(h, 4) != IntPtr.Zero)}"); }
            return true;
        }, IntPtr.Zero);
        return string.Join("; ", list);
    }

    /// <summary>Visible, unowned top-level windows of this process. An unmanaged pop-up made by the engine would add one.</summary>
    private int VisibleTopLevelWindows()
    {
        // The window an engine makes for a default pop-up belongs to its BROWSER process, not to this one: look at both.
        var mine = new HashSet<uint> { (uint)Environment.ProcessId }; foreach (var p in _leases!.ProcessIds) mine.Add((uint)p);
        var n = 0;
        // Owned windows are the engine's own small chrome (for example its "pop-up blocked" chip) and do not count: an unmanaged pop-up PAGE is a top-level window nobody owns.
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out var pid); if (mine.Contains(pid) && IsWindowVisible(h) && GetWindow(h, 4) == IntPtr.Zero) n++; return true; }, IntPtr.Zero);
        return n;
    }

    private async Task RunNavCheckAsync()
    {
        _tick?.Stop();
        var k = _kernel!;
        var steps = new List<object>();
        var pass = true;
        void Step(string name, bool ok, string detail = "") { steps.Add(new { name, ok, detail }); if (!ok) pass = false; }

        var listener = new HttpListener();
        var port = 47950;
        for (; port < 48050; port++) { listener.Prefixes.Clear(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); try { listener.Start(); break; } catch (Exception) { } }
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch (Exception) { break; }
                var path = ctx.Request.Url!.AbsolutePath;
                if (path == "/r") { ctx.Response.StatusCode = 302; ctx.Response.RedirectLocation = "/b"; ctx.Response.Close(); continue; }
                var body = path switch
                {
                    "/a" => "<!doctype html><title>A</title><a id=l href='/b' style='display:block;margin:40px;font-size:30px'>go to b</a><a id=p target=_blank href='/popup' style='display:block;margin:40px;font-size:30px'>popup</a><script>if(location.hash==='#auto')window.open('/popup')</script>",
                    "/b" => "<!doctype html><title>B</title><h1>B</h1>",
                    "/popup" => "<!doctype html><title>Popup</title><h1>popup</h1>",
                    _ => "<!doctype html><title>x</title>",
                };
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "text/html; charset=utf-8"; ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
            }
        });
        string U(string p) => $"http://127.0.0.1:{port}{p}";

        async Task<bool> Until(Func<bool> cond, int ms = 8000) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(50); } return cond(); }
        WebView2Lease? LeaseOf(ResourceId id) => _leases!.TryGet(id, out var l) ? (WebView2Lease)l : null;
        string Shown() => AddressBox.Text;

        try
        {
            var tab = k.Open(new Uri(U("/a")));
            await k.ActivateAsync(tab.Id);
            var lease = LeaseOf(tab.Id)!;
            var core = lease.View.CoreWebView2;
            Step("start page shown in the address bar", await Until(() => Shown() == U("/a")), Shown() + " | windows: " + DescribeWindows());

            async Task<bool> TrustedClick(string elementId)
            {
                // The address changes before the page has been parsed; on a slow machine the element is not there yet. Wait for it.
                string? raw = null;
                for (var i = 0; i < 100 && (raw is null || raw == "null"); i++)
                {
                    raw = await core.ExecuteScriptAsync($"(()=>{{const e=document.getElementById('{elementId}'); if(!e) return null; const r=e.getBoundingClientRect(); return JSON.stringify([r.x+20,r.y+r.height/2]);}})()");
                    if (raw is null or "null") await Task.Delay(100);
                }
                var xy = JsonSerializer.Deserialize<double[]>(JsonSerializer.Deserialize<string>(raw!)!)!;
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x = xy[0], y = xy[1] }));
                foreach (var type in new[] { "mousePressed", "mouseReleased" })
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = xy[0], y = xy[1], button = "left", buttons = type == "mousePressed" ? 1 : 0, clickCount = 1 }));
                return true;
            }

            // 1. a followed link
            await TrustedClick("l");
            Step("following a link updates the address bar", await Until(() => Shown() == U("/b")), $"bar={Shown()} engine={core.Source}");
            // 2. Back and Forward
            lease.View.GoBack();
            Step("Back shows the previous address", await Until(() => Shown() == U("/a")), Shown());
            lease.View.GoForward();
            Step("Forward shows the next address", await Until(() => Shown() == U("/b")), Shown());
            // 3. a redirect: the final address, not the one that was asked for
            lease.Navigate(new Uri(U("/a")));
            await Until(() => Shown() == U("/a"));
            lease.Navigate(new Uri(U("/r")));
            Step("a redirect shows where it ended up, not where it started", await Until(() => Shown() == U("/b")), Shown());

            // 4. deliberate editing is never overwritten
            lease.Navigate(new Uri(U("/a")));
            await Until(() => Shown() == U("/a"));
            AddressBox.Text = "half typed sear";      // a person's typing (TextChanged, not SetAddress)
            await Task.Delay(300);                     // typing raises TextChanged at once; a programmatic set raises it a moment later
            lease.Navigate(new Uri(U("/b")));           // a script or redirect moves the page under them
            await Until(() => core.Source == U("/b"), 6000);
            await Task.Delay(600);
            Step("what the person is typing is not overwritten by a navigation", Shown() == "half typed sear", Shown());
            AddressBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            AddressBox.Text = "half typed sear";
            OnAddressLostFocus(AddressBox, new Microsoft.UI.Xaml.RoutedEventArgs());
            Step("abandoning the edit shows the real address again", Shown() == U("/b"), Shown());

            // 5. an agent navigating in the background never touches the bar
            var before = Shown();
            var session = await _agents!.OpenAsync(new AgentManifest { Agent = "nav-check", AllowDomains = ["127.0.0.1"], Actions = [AgentAction.Navigate], MaxLivePages = 2, SessionMinutes = 10, DestructiveActions = "deny" }, default);
            var r = await _agents.ExecuteAsync(session, new AgentRequest(AgentAction.Navigate, U("/popup")), default);
            await Task.Delay(1500);
            Step("background agent navigation succeeded", r.Ok, r.Message + " | windows: " + DescribeWindows());
            Step("…and did not change the address bar or the tab in front", Shown() == before && k.Active?.Id == tab.Id, $"{Shown()} active={k.Active?.Id == tab.Id}");
            await _agents.StopAsync(session, default);

            // 6. new windows
            var tabsBefore = k.Tabs.Count;
            var windowsBefore = VisibleTopLevelWindows();
            // Runtime.evaluate with userGesture:false is a script with no click or key press behind it (ExecuteScriptAsync counts as one).
            lease.Navigate(new Uri(U("/a#auto")));      // the classic pop-up: the page opens a window as it loads, nobody clicked anything
            await Until(() => (core.Source ?? "").EndsWith("#auto"), 6000);
            await Task.Delay(1500);
            Step("a pop-up nobody clicked for is blocked (no new tab, no window)", k.Tabs.Count == tabsBefore, $"tabs {tabsBefore}->{k.Tabs.Count}; {_lastPopupDiag}");
            Step("…and no window of any kind appeared outside the app's own", VisibleTopLevelWindows() == windowsBefore, $"visible windows {windowsBefore}->{VisibleTopLevelWindows()}: {DescribeWindows()}");
            Step("…and the person is told", StatusText.Text.StartsWith("Blocked a pop-up", StringComparison.Ordinal), StatusText.Text);

            lease.Navigate(new Uri(U("/a")));
            await Until(() => Shown() == U("/a"));
            var ws = tab.WorkspaceId;
            await TrustedClick("p");
            var opened = await Until(() => k.Tabs.Count == tabsBefore + 1, 6000);
            var popupTab = k.Tabs.FirstOrDefault(t => t.Id != tab.Id && t.WorkspaceId == ws && t.Url.AbsolutePath.StartsWith("/popup", StringComparison.Ordinal));
            Step("a clicked link with target=_blank becomes a managed tab", opened && popupTab is not null, $"tabs={k.Tabs.Count}");
            Step("…in the same workspace as its opener", popupTab?.WorkspaceId == ws);
            Step("…and it is a tab, not a window (no extra top-level window)", VisibleTopLevelWindows() == windowsBefore, $"visible windows {windowsBefore}->{VisibleTopLevelWindows()}");
            if (popupTab is not null) await Until(() => k.Active?.Id == popupTab.Id && Shown() == popupTab.Url.ToString(), 6000);
            Step("…brought to the front through the normal path (a managed renderer, address shown)", popupTab is not null && k.Active?.Id == popupTab.Id && LeaseOf(popupTab.Id) is not null && Shown() == popupTab.Url.ToString(), Shown());
            if (popupTab is not null) await k.CloseAndSelectNextAsync(popupTab.Id);
            await Until(() => k.Active?.Id == tab.Id, 6000);
            Step("closing it returns to the opener in the same workspace", k.Active?.Id == tab.Id, $"{k.Active?.Id == tab.Id}");

            // 7. the browser process dies: the page is replaced, not left dead
            var deadLease = LeaseOf(tab.Id)!;
            var pid = (int)deadLease.View.CoreWebView2.BrowserProcessId;
            System.Diagnostics.Process.GetProcessById(pid).Kill();
            var recovered = await Until(() => LeaseOf(tab.Id) is { } l && !ReferenceEquals(l, deadLease) && k.Active?.Id == tab.Id, 20000);
            Step("a killed browser process is replaced with a new renderer for the page in front", recovered, $"{StatusText.Text}; state={tab.State} active={k.Active?.Id == tab.Id} lease={(LeaseOf(tab.Id) is null ? "none" : ReferenceEquals(LeaseOf(tab.Id), deadLease) ? "the dead one" : "new")}");
            if (recovered)
            {
                var fresh = LeaseOf(tab.Id)!;
                Step("…and the page loads again", await Until(() => fresh.View.CoreWebView2?.Source == U("/a"), 15000), fresh.View.CoreWebView2?.Source ?? "");
                Step("…and the address bar still shows it", Shown() == U("/a"), Shown());
            }
        }
        catch (Exception ex) { Step("no exception", false, ex.ToString()); }
        finally { try { listener.Stop(); listener.Close(); } catch (Exception) { } }

        var result = new { pass, steps, scope = "Real WebView2 and the real address bar. Popup clicks are trusted DevTools input events. Not covered: OAuth pop-ups that need window.opener (they are opened as ordinary tabs without one)." };
        await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "nav-check.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
