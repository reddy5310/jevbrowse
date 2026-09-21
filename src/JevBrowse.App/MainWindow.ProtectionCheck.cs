using System.Net;
using System.Text;
using System.Text.Json;
using JevBrowse.App.Renderer;
using JevBrowse.Domain;

namespace JevBrowse.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// Real engine: unfinished work is detected where a person would lose it. Typing in a password-only page, typing inside an iframe, a file upload in flight, and a
    /// long silent video each keep the tab awake (the scheduler is refused), and an idle page is left alone. Input comes from the DevTools protocol, so it is trusted input.
    /// </summary>
    private async Task RunProtectionCheckAsync()
    {
        _tick?.Stop();
        var k = _kernel!;
        var steps = new List<object>(); var pass = true;
        void Step(string name, bool ok, string detail = "") { steps.Add(new { name, ok, detail }); if (!ok) pass = false; }
        async Task<bool> Until(Func<bool> cond, int ms = 10000) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(100); } return cond(); }
        WebView2Lease Lease(VirtualTab t) => (WebView2Lease)(_leases!.TryGet(t.Id, out var l) ? l : throw new InvalidOperationException("no renderer"));

        var listener = new HttpListener(); var port = 47970;
        for (; port < 48000; port++) { listener.Prefixes.Clear(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); try { listener.Start(); break; } catch (Exception) { } }
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext c; try { c = await listener.GetContextAsync(); } catch (Exception) { break; }
                var path = c.Request.Url!.AbsolutePath;
                if (path == "/slow") { try { using var _ = c.Request.InputStream; await Task.Delay(7000); } catch (Exception) { } try { c.Response.StatusCode = 200; c.Response.Close(); } catch (Exception) { } continue; }
                if (path == "/slowimg") { try { await Task.Delay(6000); c.Response.ContentType = "image/gif"; var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"); c.Response.ContentLength64 = gif.Length; await c.Response.OutputStream.WriteAsync(gif); c.Response.Close(); } catch (Exception) { } continue; }
                var body = path switch
                {
                    "/late" => "<!doctype html><title>late</title><input id=i style='margin:30px;width:300px;height:30px'><img src='/slowimg' width=1 height=1>",
                    "/idle" => "<!doctype html><title>idle</title><p>nothing to lose here</p>",
                    "/pw" => "<!doctype html><title>pw</title><input id=p type=password style='margin:30px;width:300px;height:30px'>",
                    "/frame" => "<!doctype html><title>frame</title><iframe src='/pw' style='position:absolute;left:20px;top:20px;width:400px;height:200px;border:0'></iframe>",
                    "/up" => "<!doctype html><title>up</title><script>window.doUpload = () => { const f = new FormData(); f.append('file', new Blob([new Uint8Array(200000)]), 'big.bin'); return fetch('/slow', { method: 'POST', body: f }); };</script>",
                    "/vid" => "<!doctype html><title>vid</title><canvas id=c width=640 height=360></canvas><video id=v width=640 height=360 muted></video><script>const c=document.getElementById('c'),g=c.getContext('2d');let n=0;setInterval(()=>{g.fillStyle='hsl('+(n++*5%360)+',80%,50%)';g.fillRect(0,0,640,360);},50);const v=document.getElementById('v');v.srcObject=c.captureStream(20);v.muted=true;window.startVideo=()=>v.play();window.stopVideo=()=>v.pause();</script>",
                    _ => "<!doctype html><title>x</title>",
                };
                var bytes = Encoding.UTF8.GetBytes(body);
                c.Response.ContentType = "text/html; charset=utf-8"; c.Response.ContentLength64 = bytes.Length;
                await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
            }
        });
        string U(string p) => $"http://127.0.0.1:{port}{p}";

        async Task Click(WebView2Lease l, double x, double y)
        {
            await l.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x, y }));
            foreach (var type in new[] { "mousePressed", "mouseReleased" })
                await l.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button = "left", buttons = type == "mousePressed" ? 1 : 0, clickCount = 1 }));
        }
        async Task<VirtualTab> Open(string path)
        {
            var t = k.Open(new Uri(U(path)));
            await k.ActivateAsync(t.Id);
            await Until(() => (Lease(t).View.CoreWebView2.Source ?? "").StartsWith("http"), 10000);
            await Task.Delay(1200);
            return t;
        }
        bool Has(VirtualTab t, ProtectionFlags f) => (t.Protection & f) != 0;

        try
        {
            var parked = k.Open(new Uri(U("/idle")));   // somewhere else to be looking, so the tab under test is in the BACKGROUND when the scheduler tries it
            await k.ActivateAsync(parked.Id);
            await Task.Delay(800);

            // idle page: nothing reported, and the scheduler may put it to sleep
            var idle = await Open("/idle");
            await k.ActivateAsync(parked.Id);
            await Task.Delay(1500);
            Step("an idle page reports no unfinished work", idle.Protection == ProtectionFlags.None, idle.Protection.ToString());

            // password-only page
            var pw = await Open("/pw");
            await Click(Lease(pw), 60, 50);
            await Lease(pw).View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = "s3cret" }));
            Step("typing into a password-only page is unfinished work", await Until(() => Has(pw, ProtectionFlags.DirtyForm), 5000), pw.Protection.ToString());
            await k.ActivateAsync(parked.Id);
            var pwSleep = await k.VirtualizeAsync(pw.Id, Cause.Scheduler);
            Step("…and the scheduler is refused", !pwSleep.Allowed, pwSleep.Reason);

            // typing inside an iframe
            var fr = await Open("/frame");
            await Click(Lease(fr), 100, 70);     // inside the iframe input (frame at 20,20; input at 30,30 with size 300x30)
            await Lease(fr).View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = "typed in a frame" }));
            Step("typing inside an iframe is unfinished work", await Until(() => Has(fr, ProtectionFlags.DirtyForm), 5000), fr.Protection.ToString());

            // upload in flight
            var up = await Open("/up");
            _ = Lease(up).View.CoreWebView2.ExecuteScriptAsync("window.doUpload()");
            Step("an upload in flight is unfinished work", await Until(() => Has(up, ProtectionFlags.UploadActive), 6000), up.Protection.ToString());
            await k.ActivateAsync(parked.Id);
            var upSleep = await k.VirtualizeAsync(up.Id, Cause.Scheduler);
            Step("…and the scheduler is refused", !upSleep.Allowed, upSleep.Reason);
            Step("…and it is released when the upload finishes", await Until(() => !Has(up, ProtectionFlags.UploadActive), 20000), up.Protection.ToString());

            // typing while the page is STILL LOADING must survive the load finishing
            var late = await Open("/late");
            await Click(Lease(late), 100, 70);
            await Lease(late).View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = "typed during load" }));
            var typedWhileLoading = await Until(() => Has(late, ProtectionFlags.DirtyForm), 4000);
            var stillLoading = JsonSerializer.Deserialize<string>(await Lease(late).View.CoreWebView2.ExecuteScriptAsync("document.readyState")) != "complete";
            Step("typing while the page is still loading is unfinished work", typedWhileLoading && stillLoading, $"{late.Protection}; loading={stillLoading}");
            for (var i = 0; i < 150; i++)   // an async wait: blocking here would deadlock the window's own thread
            {
                if (JsonSerializer.Deserialize<string>(await Lease(late).View.CoreWebView2.ExecuteScriptAsync("document.readyState")) == "complete") break;
                await Task.Delay(100);
            }
            await Task.Delay(1500);
            Step("…and it is STILL protected after the load completes", Has(late, ProtectionFlags.DirtyForm), late.Protection.ToString());

            // silent video
            var vid = await Open("/vid");
            await Lease(vid).View.CoreWebView2.ExecuteScriptAsync("window.startVideo()");
            Step("a playing video with no sound is unfinished work", await Until(() => Has(vid, ProtectionFlags.VideoPlaying), 8000), vid.Protection.ToString());
            await k.ActivateAsync(parked.Id);
            var vidSleep = await k.VirtualizeAsync(vid.Id, Cause.Scheduler);
            Step("…and the scheduler is refused", !vidSleep.Allowed, vidSleep.Reason);
            await k.ActivateAsync(vid.Id);
            await Lease(vid).View.CoreWebView2.ExecuteScriptAsync("window.stopVideo()");
            // buffering: the engine reports "not enough data" while playing; that is not a pause
            await Lease(vid).View.CoreWebView2.ExecuteScriptAsync("window.startVideo(); Object.defineProperty(document.getElementById('v'), 'readyState', { get: () => 2 });");
            await Task.Delay(10000);   // more than two polling periods
            Step("a playing video that is buffering stays protected", Has(vid, ProtectionFlags.VideoPlaying), vid.Protection.ToString());
            await Lease(vid).View.CoreWebView2.ExecuteScriptAsync("window.stopVideo()");
            Step("…and it is released when the video is paused", await Until(() => !Has(vid, ProtectionFlags.VideoPlaying), 12000), vid.Protection.ToString());

            // stopped agents, in both grantable containers: retired, profile deleted, nothing selectable, the person undisturbed
            var ephemeralRoot = Path.Combine(DataDir, "profiles", "ephemeral");
            int Dirs() => Directory.Exists(ephemeralRoot) ? Directory.GetDirectories(ephemeralRoot).Length : 0;
            foreach (var container in new[] { IdentityContainer.Disposable, IdentityContainer.Private })
            {
                await k.ActivateAsync(parked.Id);
                var baseline = Dirs();
                var ceiling = new JevBrowse.AgentGateway.AgentCeiling { Limits = new JevBrowse.AgentGateway.AgentManifest { Agent = "ceiling", AllowDomains = ["127.0.0.1"], Actions = [AgentAction.Navigate, AgentAction.Read], SessionMinutes = 10, MaxLivePages = 2, Container = container } };
                _agentHost = new JevBrowse.AgentGateway.LocalAgentHost(_agents!, ceiling);
                var (session, _) = await _agentHost.GrantAsync(new JevBrowse.AgentGateway.AgentManifest { Agent = "retire-check", AllowDomains = ["127.0.0.1"], Actions = [AgentAction.Navigate, AgentAction.Read], SessionMinutes = 10, MaxLivePages = 2, Container = container });
                var nav = await _agents!.ExecuteAsync(session, new JevBrowse.AgentGateway.AgentRequest(AgentAction.Navigate, U("/idle")), default);
                var agentTab = k.Tabs.FirstOrDefault(t => session.Pages.Contains(t.Id));
                var wsContainer = k.Workspaces.FirstOrDefault(w => w.Id == session.WorkspaceId)?.Container;
                Step($"[{container}] the agent worked in a workspace of that container", nav.Ok && agentTab is not null && wsContainer == container && Dirs() == baseline + 1, $"{nav.Message}; container={wsContainer}; profiles {baseline}->{Dirs()}");
                await _agentHost.StopAsync(session);
                var retired = await Until(() => _agentCleanupDone.Contains(session.Id), 20000);
                Step($"[{container}] a stopped agent is retired and its profile deleted", retired && k.Workspaces.All(w => w.Id != session.WorkspaceId) && k.Tabs.All(t => t.Id != agentTab!.Id) && Dirs() == baseline, $"done={retired}; profiles={Dirs()} (baseline {baseline})");
                var threw = false; try { await k.ActivateAsync(agentTab!.Id); } catch (Exception) { threw = true; }
                Step($"[{container}] its page cannot be selected again (fails cleanly)", threw && k.Active?.Id == parked.Id, $"threw={threw}");
                _agentHost.Dispose(); _agentHost = null;
            }
        }
        catch (Exception ex) { Step("no exception", false, ex.ToString()); }
        finally { try { listener.Stop(); listener.Close(); } catch (Exception) { } }

        var result = new { pass, steps, scope = "Real WebView2, trusted DevTools input. Detection is in the page script; the scheduler veto is the kernel's." };
        await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "protection-check.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
