namespace JevBrowse.App;

/// <summary>
/// First-run and Help page, rendered locally (NavigateToString; no network). Dark, layered, with depth, and it
/// teaches the three ideas that make JevBrowse different before the user opens a second tab.
/// </summary>
public static class WelcomePage
{
    public const string Url = "jev://welcome";

    public static string Html(bool aiConfigured, bool jevConfigured) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Welcome to JevBrowse</title>
        <style>
          /* Same tokens as the app (App.xaml), same two schemes. The page follows prefers-color-scheme, which the browser sets
             from the app's theme, so a light window never shows a dark slab. */
          :root { color-scheme:dark; --bg:#0b0d12; --panel:#121620; --line:#1e2533; --text:#e6e9ef; --muted:#8b94a7; --teal:#20d6c6; --violet:#8b7bff; --amber:#ffb454; --rose:#ff5d8f;
                  --glow:#17223a; --card1:#151a26; --card2:#0f131c; --body:#c9d0dc; --chip:#0b0e15; --btn:#131826; --btn-on:#08101a; --hot:#3ddc84; --cold:#4aa3ff; --virt:#6b7385 }
          @media (prefers-color-scheme: light) {
            :root { color-scheme:light; --bg:#f3f5f9; --panel:#ffffff; --line:#d5dbe6; --text:#121622; --muted:#485065; --teal:#0a7b71; --violet:#5b4fd6; --amber:#b26a00; --rose:#b3261e;
                    --glow:#dbe6f7; --card1:#ffffff; --card2:#f3f5f9; --body:#2b3346; --chip:#edf0f6; --btn:#edf0f6; --btn-on:#ffffff; --hot:#1e8e4e; --cold:#1f6fb8; --virt:#6b7488 }
          }
          /* The card tilt is a response to the pointer; people who ask the OS for less motion get none of it. */
          @media (prefers-reduced-motion: reduce) { .card, .card:hover { transition:none !important; transform:none !important } }
          * { box-sizing:border-box } html,body { margin:0; background:radial-gradient(1200px 600px at 20% -10%, var(--glow) 0%, var(--bg) 55%), var(--bg); color:var(--text); font:15px/1.55 "Segoe UI Variable", "Segoe UI", system-ui, sans-serif; }
          .wrap { max-width:1080px; margin:0 auto; padding:56px 28px 80px; perspective:1200px }
          h1 { font-size:44px; letter-spacing:-.02em; margin:0 0 6px; background:linear-gradient(90deg, var(--teal), var(--violet)); -webkit-background-clip:text; background-clip:text; color:transparent }
          .tag { color:var(--muted); font-size:17px; margin-bottom:36px }
          .grid { display:grid; grid-template-columns:repeat(auto-fit, minmax(300px, 1fr)); gap:18px }
          .card { background:linear-gradient(180deg, var(--card1), var(--card2)); border:1px solid var(--line); border-radius:18px; padding:22px 22px 18px; position:relative; transform:translateZ(0) rotateX(0); transition:transform .35s cubic-bezier(.2,.8,.2,1), box-shadow .35s; box-shadow:0 18px 40px -24px rgba(0,0,0,.9), inset 0 1px 0 rgba(255,255,255,.04) }
          .card:hover { transform:translateZ(24px) rotateX(2deg); box-shadow:0 30px 60px -28px rgba(32,214,198,.35), 0 0 0 1px rgba(32,214,198,.25) }
          .card h2 { margin:0 0 8px; font-size:19px } .card p { margin:0 0 8px; color:var(--body) } .card .k { display:inline-block; padding:2px 8px; border:1px solid var(--line); border-radius:8px; font-family:Consolas, monospace; font-size:12.5px; background:var(--chip); color:var(--teal) }
          .dots span { display:inline-block; width:10px; height:10px; border-radius:50%; margin-right:8px; box-shadow:0 0 12px currentColor } .hot{color:var(--hot);background:var(--hot)} .warm{color:var(--amber);background:var(--amber)} .cold{color:var(--cold);background:var(--cold)} .virt{color:var(--virt);background:var(--virt)}
          .glow { position:absolute; inset:-1px; border-radius:18px; pointer-events:none; background:radial-gradient(400px 120px at 10% 0%, rgba(139,123,255,.18), transparent 60%) }
          .row { display:flex; gap:12px; flex-wrap:wrap; margin-top:28px } .btn { padding:10px 16px; border-radius:12px; border:1px solid var(--line); background:var(--btn); color:var(--text); text-decoration:none; font-weight:600 } .btn.primary { background:linear-gradient(90deg, var(--teal), var(--violet)); color:var(--btn-on); border:none }
          .fine { color:var(--muted); font-size:13px; margin-top:30px } .ok{color:var(--teal)} .no{color:var(--rose)}
          table { width:100%; border-collapse:collapse; margin-top:6px } td { padding:6px 0; border-top:1px solid var(--line); color:var(--body) } td:first-child { color:var(--text); font-weight:600; width:38% }
        </style></head><body><div class="wrap">
          <h1>Open many. Run few. Keep context.</h1>
          <div class="tag">JevBrowse keeps every tab you open, but only the ones you are using get a renderer. Here is everything you need for the first ten minutes.</div>
          <div class="grid">
            <div class="card"><div class="glow"></div><h2>1 · Tabs are durable, renderers are borrowed</h2><p>The sidebar lists every tab. The dot says whether it holds a live page:</p>
              <p class="dots"><span class="hot"></span>live, in front &nbsp; <span class="warm"></span>live, warm &nbsp; <span class="cold"></span>live, low priority &nbsp; <span class="virt"></span>virtual (no memory used)</p>
              <p>Click a grey tab and it comes back in about a third of a second with its scroll position. Press <span class="k">Explain</span> on any tab to see exactly why the browser did what it did, and override it.</p></div>
            <div class="card"><div class="glow"></div><h2>2 · Ctrl+K does everything</h2><p><span class="k">Ctrl+K</span> opens the command palette. Type what you want:</p>
              <p><span class="k">hibernate everything except current</span> · <span class="k">search browser memory</span> · <span class="k">restore yesterday's context</span> · <span class="k">open in disposable identity</span> · <span class="k">grant Claude Code localhost + GitHub for 30 minutes</span></p></div>
            <div class="card"><div class="glow"></div><h2>3 · Shield blocks, and shows its work</h2><p>Ads, trackers and empty ad boxes are removed in four layers. The <span class="k">Shield</span> button lists every blocked request and its rule, and turns Shield off for a site in one click. On YouTube, a site module strips ad definitions before the player sees them and measures what got through.</p></div>
            <div class="card"><div class="glow"></div><h2>4 · Workspaces and Time Travel</h2><p>Create a workspace per activity (Job search, Trip, Dev). Switching changes what the browser keeps live, not what it forgets. The clock icon restores any earlier context, loading only the tab you were on.</p></div>
            <div class="card"><div class="glow"></div><h2>5 · Privacy is the default</h2><p>Pages are classified <b>PUBLIC · AUTHENTICATED · SENSITIVE · SECRET</b> (see the badge left of the address bar). Higher classes persist less. A password field on the page means nothing is stored and nothing is sent anywhere, whatever else is on.</p>
              <table><tr><td>Identity containers</td><td>Personal, Work, Dev, Disposable, Private — separate cookies and storage</td></tr><tr><td>Permissions</td><td>Notifications quiet by default; grants can be "for 1 hour"</td></tr></table></div>
            <div class="card"><div class="glow"></div><h2>6 · AI is a decision engine, and it is off</h2><p>Turn it on in <span class="k">Brain</span>. Jev answers yes/no and multiple-choice questions with probabilities (page class, ad slots, search ranking) and every answer is logged as numbers. <span class="k">Ask</span> summarizes a page via OpenRouter only after showing you what will be sent and what was redacted.</p>
              <p>Status: OpenRouter <span class="{{(aiConfigured ? "ok" : "no")}}">{{(aiConfigured ? "configured" : "not configured")}}</span> · Jev <span class="{{(jevConfigured ? "ok" : "no")}}">{{(jevConfigured ? "configured" : "not configured")}}</span></p></div>
          </div>
          <div class="row"><a class="btn primary" href="https://en.wikipedia.org/wiki/Web_browser">Open a page and try it</a><a class="btn" href="https://github.com/reddy5310/jevbrowse">Source and documentation</a></div>
          <div class="fine">Product modes (top-left) hide what you don't need: <b>Simple</b> for tabs + Shield, <b>Focus</b> for reading, <b>Power</b> for everything, <b>Developer</b> for DevSpace, <b>Agent</b> for the Agent Gateway, <b>Private</b> for an ephemeral session. This page is generated locally and makes no network requests.</div>
        </div></body></html>
        """;
}
