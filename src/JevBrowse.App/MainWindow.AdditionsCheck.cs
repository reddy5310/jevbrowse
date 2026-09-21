using System.Net;
using System.Text;
using System.Text.Json;
using JevBrowse.App.Renderer;
using JevBrowse.Domain;
using JevBrowse.TrustOS;

namespace JevBrowse.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// Real engine checks for the private-alpha additions: remembered permissions reset and are asked again; clearing website data touches one profile only;
    /// a Private download is asked about first (cancel saves nothing); and what a popup-based sign-in actually does in this build (recorded, not assumed).
    /// </summary>
    private async Task RunAdditionsCheckAsync()
    {
        _tick?.Stop();
        var k = _kernel!;
        var steps = new List<object>(); var pass = true;
        void Step(string name, bool ok, string detail = "") { steps.Add(new { name, ok, detail }); if (!ok) pass = false; }
        async Task<bool> Until(Func<bool> cond, int ms = 10000) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(50); } return cond(); }
        WebView2Lease? LeaseOf(ResourceId id) => _leases!.TryGet(id, out var l) ? (WebView2Lease)l : null;

        var downloads = Path.Combine(DataDir, "benchmarks", "downloads");
        Directory.CreateDirectory(downloads);
        var received = new List<string>();
        // Two origins, so a "sign-in" popup is genuinely cross-origin. Port A serves the page and the file; port B is the identity provider.
        HttpListener Serve(int start, Func<HttpListenerContext, Task> handle)
        {
            for (var p = start; p < start + 100; p++)
            {
                var l = new HttpListener(); l.Prefixes.Add($"http://127.0.0.1:{p}/");
                try { l.Start(); _ = Task.Run(async () => { while (l.IsListening) { HttpListenerContext c; try { c = await l.GetContextAsync(); } catch (Exception) { break; } await handle(c); } }); return l; } catch (Exception) { l.Close(); }
            }
            throw new InvalidOperationException("no free port");
        }
        static async Task Send(HttpListenerContext c, string type, string body, string? disposition = null)
        {
            var b = Encoding.UTF8.GetBytes(body); c.Response.ContentType = type; c.Response.ContentLength64 = b.Length;
            if (disposition is not null) c.Response.AddHeader("Content-Disposition", disposition);
            await c.Response.OutputStream.WriteAsync(b); c.Response.Close();
        }
        int portB = 0;
        var idp = Serve(47960, async c =>
        {
            // The "identity provider": reports whether it has an opener, and tries to hand a token back to it.
            await Send(c, "text/html; charset=utf-8", "<!doctype html><title>idp</title><script>const hasOpener = !!window.opener; if (window.opener) window.opener.postMessage({token:'T'}, '*'); document.title = 'idp opener=' + hasOpener;</script><p>signing in</p>");
        });
        portB = int.Parse(idp.Prefixes.First().Split(':')[2].TrimEnd('/'));
        var app = Serve(47990, async c =>
        {
            var path = c.Request.Url!.AbsolutePath;
            if (path == "/file") { await Send(c, "application/octet-stream", "hello", "attachment; filename=\"note.bin\""); return; }
            await Send(c, "text/html; charset=utf-8", $"<!doctype html><title>app</title><a id=login target=_blank href='http://127.0.0.1:{portB}/auth' style='display:block;margin:40px;font-size:30px'>Sign in</a><a id=dl href='/file' style='display:block;margin:40px;font-size:30px'>download</a><script>window.gotToken=false; addEventListener('message', e => {{ if (e.data && e.data.token) window.gotToken = true; }});</script>");
        });
        var portA = int.Parse(app.Prefixes.First().Split(':')[2].TrimEnd('/'));
        string A(string p) => $"http://127.0.0.1:{portA}{p}";

        try
        {
            // ---------------- 1. remembered permission decisions reset, and the next request is decided normally
            var asked = 0;
            var perms = new JevBrowse.App.Trust.PermissionAdapter(new JevBrowse.Storage.SitePermissionsRepository(_db!), (_, _, _) => { asked++; return Task.FromResult(PermissionChoice.AllowAlways); });
            var origin = new Uri("https://meet.example.com/");
            await perms.DecideAsync(IdentityContainer.Personal, ContextId.Default, origin, PermissionKind.Camera);
            await perms.DecideAsync(IdentityContainer.Personal, ContextId.Default, origin, PermissionKind.Camera);
            Step("a remembered decision is not asked about again", asked == 1, $"asked {asked}");
            Step("it is listed for this origin and profile", perms.Saved(IdentityContainer.Personal, ContextId.Default, origin).Any(g => g.Kind == PermissionKind.Camera && g.Allowed));
            Step("…and not for another profile", perms.Saved(IdentityContainer.Work, ContextId.Default, origin).Count == 0);
            perms.Reset(IdentityContainer.Personal, ContextId.Default, origin, PermissionKind.Camera);
            await perms.DecideAsync(IdentityContainer.Personal, ContextId.Default, origin, PermissionKind.Camera);
            Step("after Reset the next request is asked again", asked == 2, $"asked {asked}");
            var session = ContextId.New();
            await perms.DecideAsync(IdentityContainer.Private, session, origin, PermissionKind.Microphone);
            Step("a Private session's in-memory decision is listed and resettable", perms.Saved(IdentityContainer.Private, session, origin).Count == 1 && perms.Reset(IdentityContainer.Private, session, origin) == 1 && perms.Saved(IdentityContainer.Private, session, origin).Count == 0);

            // ---------------- 2. clearing website data: one profile, not the others
            var work = k.CreateWorkspace("Work", IdentityContainer.Work);
            var personalTab = k.Open(new Uri(A("/")));
            await k.ActivateAsync(personalTab.Id);
            var workTab = k.OpenIn(work.Id, new Uri(A("/")));
            await k.ActivateAsync(workTab.Id);
            var pl = LeaseOf(personalTab.Id)!; var wl = LeaseOf(workTab.Id)!;
            await Until(() => (pl.View.CoreWebView2.Source ?? "").StartsWith("http") && (wl.View.CoreWebView2.Source ?? "").StartsWith("http"));
            await Task.Delay(800);
            foreach (var l in new[] { pl, wl })
            {
                var m = l.View.CoreWebView2.CookieManager;
                m.AddOrUpdateCookie(m.CreateCookie("session", "signed-in", "127.0.0.1", "/"));
                await l.View.CoreWebView2.ExecuteScriptAsync("localStorage.setItem('k','v')");
            }
            async Task<(int cookies, string local)> Read(WebView2Lease l) =>
                ((await l.View.CoreWebView2.CookieManager.GetCookiesAsync(A("/"))).Count(c => c.Name == "session"), JsonSerializer.Deserialize<string>(await l.View.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('k') ?? 'none'")) ?? "");
            var before = (await Read(pl), await Read(wl));
            Step("both profiles have a cookie and website storage to begin with", before.Item1 == (1, "v") && before.Item2 == (1, "v"), $"{before}");
            await ClearProfileWebsiteDataAsync(pl.View.CoreWebView2);
            var afterP = await Read(pl); var afterW = await Read(wl);
            Step("clearing the Personal profile removes its cookie and website storage (signed out)", afterP == (0, "none"), $"{afterP}");
            Step("…and leaves the Work profile alone", afterW == (1, "v"), $"{afterW}");

            // ---------------- 3. Private downloads are asked about first
            var priv = k.CreateWorkspace("Priv", IdentityContainer.Private);
            await k.SwitchWorkspaceAsync(priv.Id);
            var pt = k.OpenIn(priv.Id, new Uri(A("/")));
            await k.ActivateAsync(pt.Id);
            _leases!.DownloadPathOverride = downloads;   // measurement only: no engine save dialog
            var privLease = LeaseOf(pt.Id)!;
            privLease.DownloadPathOverride = downloads;
            await Until(() => (privLease.View.CoreWebView2.Source ?? "").StartsWith("http"));
            async Task ClickDownload(WebView2Lease l)
            {
                var raw = await l.View.CoreWebView2.ExecuteScriptAsync("(()=>{const r=document.getElementById('dl').getBoundingClientRect();return JSON.stringify([r.x+20,r.y+r.height/2]);})()");
                var xy = JsonSerializer.Deserialize<double[]>(JsonSerializer.Deserialize<string>(raw)!)!;
                await l.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x = xy[0], y = xy[1] }));
                foreach (var type in new[] { "mousePressed", "mouseReleased" })
                    await l.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = xy[0], y = xy[1], button = "left", buttons = type == "mousePressed" ? 1 : 0, clickCount = 1 }));
            }
            // The real prompt, on screen: it says what the person needs to know, and dismissing it (the Cancel/Esc path) saves nothing.
            DownloadAnswerForCheck = null;
            await ClickDownload(privLease);
            Microsoft.UI.Xaml.Controls.ContentDialog? shown = null;
            await Until(() =>
            {
                foreach (var pop in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot))
                    if (pop.Child is Microsoft.UI.Xaml.Controls.ContentDialog d) { shown = d; return true; }
                return false;
            }, 8000);
            var promptText = shown?.Content is Microsoft.UI.Xaml.Controls.TextBlock tb ? tb.Text : "";
            Step("a Private-session download shows a prompt before anything is saved", shown is not null && shown.Title?.ToString() == "Save a file from a Private session?", shown?.Title?.ToString() ?? "no dialog");
            Step("…that says the file stays after the session ends and offers Cancel (the safe default)", promptText.Contains("stays on your computer after the Private session ends") && shown?.CloseButtonText == "Cancel" && shown.DefaultButton == Microsoft.UI.Xaml.Controls.ContentDialogButton.Close, promptText);
            shown?.Hide();                                        // dismissed without choosing "Continue"
            await Task.Delay(2500);
            Step("dismissing the prompt saves nothing", Directory.GetFiles(downloads).Length == 0, string.Join(",", Directory.GetFiles(downloads).Select(Path.GetFileName)));
            DownloadAnswerForCheck = false;                       // the person presses Cancel
            await ClickDownload(privLease);
            await Task.Delay(2500);
            Step("Cancel on the Private-session prompt saves nothing", Directory.GetFiles(downloads).Length == 0, string.Join(",", Directory.GetFiles(downloads).Select(Path.GetFileName)));
            DownloadAnswerForCheck = true;                        // the person continues
            await ClickDownload(privLease);
            var saved = await Until(() => Directory.GetFiles(downloads, "note*").Length == 1, 8000);
            Step("Continue lets the normal save proceed", saved);
            foreach (var f in Directory.GetFiles(downloads)) File.Delete(f);
            await k.SwitchWorkspaceAsync(ContextId.Default);
            await k.ActivateAsync(personalTab.Id);
            DownloadAnswerForCheck = false;                       // an ordinary tab is not asked at all: even a "Cancel" answer must not stop it
            var ordLease = LeaseOf(personalTab.Id)!; ordLease.DownloadPathOverride = downloads;
            await Until(() => (ordLease.View.CoreWebView2.Source ?? "").StartsWith("http"));
            await ClickDownload(ordLease);
            Step("an ordinary tab downloads as before, without a Private-session question", await Until(() => Directory.GetFiles(downloads, "note*").Length == 1, 8000));

            // ---------------- 4. what a popup-based sign-in does in this build (recorded)
            var loginLease = LeaseOf(personalTab.Id)!;
            var tabsBefore = k.Tabs.Count;
            var loginRaw = await loginLease.View.CoreWebView2.ExecuteScriptAsync("(()=>{const r=document.getElementById('login').getBoundingClientRect();return JSON.stringify([r.x+20,r.y+r.height/2]);})()");
            var lxy = JsonSerializer.Deserialize<double[]>(JsonSerializer.Deserialize<string>(loginRaw)!)!;
            await loginLease.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x = lxy[0], y = lxy[1] }));
            foreach (var type in new[] { "mousePressed", "mouseReleased" })
                await loginLease.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = lxy[0], y = lxy[1], button = "left", buttons = type == "mousePressed" ? 1 : 0, clickCount = 1 }));
            await Until(() => k.Tabs.Count == tabsBefore + 1, 6000);
            var idpTab = k.Tabs.FirstOrDefault(t => t.Url.Port == portB);
            await Task.Delay(2500);
            var idpTitle = idpTab is not null && LeaseOf(idpTab.Id) is { } il ? il.View.CoreWebView2.DocumentTitle : "";
            var gotToken = JsonSerializer.Deserialize<bool>(await loginLease.View.CoreWebView2.ExecuteScriptAsync("window.gotToken"));
            Step("RECORDED: a popup sign-in opens as a managed tab", idpTab is not null, idpTitle);
            Step("RECORDED: the sign-in page has no opener, so it cannot hand the token back (popup sign-in is unsupported in this alpha)", idpTitle.Contains("opener=false") && !gotToken, $"title='{idpTitle}' tokenReceived={gotToken}");

            // ---------------- 5. bookmarks: saved from an ordinary tab, refused from a Private one, and the chosen search engine is used
            await k.SwitchWorkspaceAsync(priv.Id); await k.ActivateAsync(pt.Id);
            var refused = !TryBookmarkActive(out _, out var refusal);
            Step("a Private session cannot add a bookmark (it promises to leave nothing)", refused && refusal.Contains("Private"), refusal);
            await k.SwitchWorkspaceAsync(ContextId.Default); await k.ActivateAsync(personalTab.Id);
            var okBm = TryBookmarkActive(out var bm, out var why);
            Step("an ordinary tab can be bookmarked", okBm && bm is not null && bm.Url == personalTab.Url.AbsoluteUri, why);
            if (okBm) { BookmarkCurrentPage(); }
            Step("the bookmark is saved", okBm && _bookmarks!.Contains(bm!.Url));
            BookmarkCurrentPage();
            Step("bookmarking the same page again removes it", !_bookmarks!.Contains(bm!.Url));
            var prefsBefore = UiPrefs.Load(DataDir);
            UiPrefs.Load(DataDir).WithSearchEngine("bing").Save(DataDir);
            var searchUrl = AddressInput.Resolve("jev test", SearchEngines.Find(UiPrefs.Load(DataDir).SearchEngine)).Url!.Host;
            prefsBefore.Save(DataDir);
            Step("the chosen search engine is what the address bar searches with", searchUrl == "www.bing.com", searchUrl);

            // ---------------- 6. history, download list and per-site zoom, from real pages and a real download
            var ordinaryUrl = new Uri(A("/")).AbsoluteUri;
            Step("an ordinary page is in the history list", await Until(() => _history!.List().Any(v => v.Url == ordinaryUrl), 8000), string.Join(",", _history!.List().Select(v => v.Url)));
            var privLease2 = LeaseOf(pt.Id)!;
            privLease2.Navigate(new Uri(A("/private-only"))); await Until(() => privLease2.View.CoreWebView2.Source.EndsWith("/private-only"), 8000); await Task.Delay(1200);
            Step("a page opened in a Private session is not in the history list", !_history.List().Any(v => v.Url.EndsWith("/private-only")), string.Join(",", _history.List().Select(v => v.Url)));
            Step("the ordinary download is in the downloads list, finished, with its file present", await Until(() => _downloads!.List().Any(d => d.Name.StartsWith("note") && d.Completed && File.Exists(d.Path)), 8000), string.Join(",", _downloads!.List().Select(d => d.Name)));
            Step("the Private-session download is listed for this session only, not written to the database", _sessionDownloads.Count >= 1 && _downloads!.List().Count == 1, $"session={_sessionDownloads.Count} stored={_downloads!.List().Count}");
            await k.SwitchWorkspaceAsync(ContextId.Default); await k.ActivateAsync(personalTab.Id);
            Zoom(1);
            var zl = LeaseOf(personalTab.Id)!;
            async Task<string> ZoomStyle() => JsonSerializer.Deserialize<string>(await zl.View.CoreWebView2.ExecuteScriptAsync("document.documentElement.style.zoom||''")) ?? "";
            var z1 = false; for (var i = 0; i < 20 && !z1; i++) { z1 = await ZoomStyle() == "1.1"; if (!z1) await Task.Delay(150); }
            Step("Ctrl+ zooms the page one step (110%)", z1, await ZoomStyle());
            Step("…and remembers it for the site", Math.Abs(_siteZoom!.Get(personalTab.Url.Host) - 1.1) < 0.001);
            zl.Navigate(new Uri(A("/"))); await Task.Delay(1500);
            var again = false; for (var i = 0; i < 40 && !again; i++) { again = await ZoomStyle() == "1.1"; if (!again) await Task.Delay(250); }
            Step("a fresh load of the same site comes back at 110%", again, await ZoomStyle());
            Zoom(0, reset: true);
            Step("reset returns to 100% and forgets the site's zoom", Math.Abs(_siteZoom.Get(personalTab.Url.Host) - 1.0) < 0.001);
            await k.SwitchWorkspaceAsync(priv.Id); await k.ActivateAsync(pt.Id);
            Zoom(1);
            Step("in a Private session zoom works but is never written", _siteZoom.Get(pt.Url.Host) == 1.0);
            var sessionBefore = _sessionDownloads.Count;
            await k.EndPrivateSessionAsync(priv.Id, ContextId.Default);
            Step("ending the Private session forgets its download names (the files stay)", sessionBefore >= 1 && _sessionDownloads.Count == 0 && Directory.GetFiles(downloads).Length >= 1, $"before={sessionBefore} after={_sessionDownloads.Count} files={Directory.GetFiles(downloads).Length}");
        }
        catch (Exception ex) { Step("no exception", false, ex.ToString()); }
        finally { try { idp.Stop(); app.Stop(); } catch (Exception) { } DownloadAnswerForCheck = null; }

        var result = new { pass, steps, scope = "Real WebView2. Downloads are saved to a check folder instead of the engine's own save UI (that UI is not exercised). Popup sign-in is a documented limitation: the check records that it does not work." };
        await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "additions-check.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Seed for the restart check: an ordinary workspace with two tabs and the second one in front, then a Private session in front of everything.</summary>
    private async Task RunResumeSeedAsync()
    {
        try
        {
            await Task.Delay(6000);
            var k = _kernel!;
            var work = k.CreateWorkspace("Work", IdentityContainer.Work);
            k.OpenIn(work.Id, new Uri(WelcomePage.Url + "?resume=first"));
            var second = k.OpenIn(work.Id, new Uri(WelcomePage.Url + "?resume=second"));
            await k.ActivateAsync(second.Id);
            await Task.Delay(2500);
            await ChangeProductModeAsync(ProductMode.Private);
            var p = k.Open(new Uri(WelcomePage.Url + "?resume=PRIVATE"));
            await k.ActivateAsync(p.Id);
            await Task.Delay(2500);
            var dir = Path.Combine(DataDir, "benchmarks"); Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "resume-seed.json"), JsonSerializer.Serialize(new { work = work.Id.ToString(), second = second.Id.ToString(), privateInFront = k.ContainerOf(k.Active!) == IdentityContainer.Private }));
        }
        catch (Exception ex) { try { await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "resume-seed-error.txt"), ex.ToString()); } catch (Exception) { } }
    }
}
