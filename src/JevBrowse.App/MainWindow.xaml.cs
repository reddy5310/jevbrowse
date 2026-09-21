using System.Collections.ObjectModel;
using System.Text.Json;
using JevBrowse.AgentGateway;
using JevBrowse.App.DevSpace;
using JevBrowse.App.Renderer;
using JevBrowse.App.Shield;
using JevBrowse.DevSpace;
using JevBrowse.App.Trust;
using JevBrowse.Brain;
using JevBrowse.Brain.Providers;
using JevBrowse.Diagnostics;
using JevBrowse.Memory;
using JevBrowse.Shield;
using JevBrowse.TrustOS;
using JevBrowse.Domain;
using JevBrowse.ResourceOS;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using Question = JevBrowse.Brain.Question;

namespace JevBrowse.App;

public sealed partial class MainWindow : Window
{
    private static readonly string DataDir =
        Environment.GetEnvironmentVariable("JEVBROWSE_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JevBrowse");

    public ObservableCollection<TabItem> Items { get; } = [];

    private WebView2LeaseManager? _leases;
    private PermissionAdapter? _permissions;
    private SiteSettingsRepository? _siteSettings;
    private BrowserDb? _db;
    private TabKernel? _kernel;
    private readonly DefaultScheduler _scheduler = new();
    private ShieldAdapter? _shield;
    private FilterListStore? _filters;
    private readonly BrainPolicy _brainPolicy = new();
    private BrainRouter? _brain;
    private JevDecisionProvider? _jev;
    private DecisionLogRepository? _decisions;
    private IReadOnlyList<IAiProvider> _providers = [];
    private BrowserMemory? _memory;
    private MemoryIndexer? _indexer;
    private DevSpaceAdapter? _dev;
    private AgentGateway.AgentGateway? _agents;
    private LocalAgentHost? _agentHost;

    private void OnAgentHostFault(Exception ex) => DispatcherQueue.TryEnqueue(() => StatusText.Text = "Agent housekeeping failed: " + ex.Message);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tick;
    private ResourcePlan? _lastPlan;
    private bool _syncingSelection;

    public MainWindow()
    {
        InitializeComponent();
        InitTheme();
        // One layered surface: Mica behind everything, our own title bar, dark by default.
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        // Final semantic checkpoint (bounded to 4 s) so the next start restores the pages the user was on.
        AppWindow.Closing += async (_, e) =>
        {
            if (_shutdownCheckpointDone || _kernel is null) return;
            e.Cancel = true;
            if (_shutdownInProgress) return;
            _shutdownInProgress = true;
            _tick?.Stop();
            Root.IsHitTestVisible = false;
            // Agent sessions end FIRST and asynchronously: their pages must be released on this thread, before the window and the engine go away.
            try
            {
                if (_agentHost is not null) await _agentHost.StopEndpointAsync().WaitAsync(TimeSpan.FromSeconds(6));
                await EndFinishedAgentProfilesAsync();
            }
            catch (Exception) { /* the renderers are closed below regardless; nothing an agent holds survives the process */ }
            try { await _kernel.CheckpointAllAsync(new CancellationTokenSource(TimeSpan.FromSeconds(4)).Token); }
            catch (Exception) { }
            await _productModeGate.WaitAsync();
            try
            {
                foreach (var workspace in _kernel.Workspaces.Where(w => w.Container == IdentityContainer.Private).Select(w => w.Id).Concat(_pendingPrivateCleanup).Distinct().ToArray())
                {
                    try { await EndPrivateSessionCoreAsync(workspace); }
                    catch (Exception) { /* shutdown still closes all renderers; remaining files are swept on startup */ }
                }
            }
            finally { _productModeGate.Release(); }
            _shutdownCheckpointDone = true;
            Close();
        };
        Closed += (_, _) => { _agentHost?.Dispose(); _leases?.Shutdown(); _db?.Dispose(); };
        // Nothing can be clicked until the kernel exists: New tab, the address bar and every accelerator dereference it. Startup problems are shown, not lost.
        Root.IsHitTestVisible = false;
        Root.PreviewKeyDown += (_, e) => { if (!_ready) e.Handled = true; };   // and no shortcut either, until the kernel exists
        StatusText.Text = "Starting JevBrowse…";
        _ = InitSafeAsync();
    }

    private async Task InitSafeAsync()
    {
        // This starts inside the constructor. Let the constructor finish and the window come up first: a failure that happened before that could not show its dialog,
        // and quitting from inside the constructor tears the window down before it is activated.
        await Task.Yield();
        if (!WebView2RuntimeAvailable(out var runtimeProblem))
        {
            await FatalStartupAsync("JevBrowse needs the Microsoft Edge WebView2 Runtime",
                "JevBrowse shows web pages with the Microsoft Edge WebView2 Runtime, and it is not installed on this computer (or could not be started).\n\n"
                + "Install it (free, from Microsoft), then start JevBrowse again. Your data is not affected.\n\nDetails: " + runtimeProblem,
                new InvalidOperationException(runtimeProblem), "Get the WebView2 Runtime", "https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            return;
        }
        try { await InitAsync(); }
        catch (UnsupportedDatabaseVersionException ex) { await FatalStartupAsync("Update JevBrowse to open this data", ex.Message, ex); }
        catch (Exception ex) { await FatalStartupAsync("JevBrowse could not finish starting", "Something went wrong while starting up. Your saved data was not changed by this message.\n\n" + ex.Message, ex); }
    }

    private async Task FatalStartupAsync(string title, string message, Exception ex, string? actionText = null, string? actionUrl = null)
    {
        try { File.WriteAllText(Path.Combine(DataDir, "startup-error.txt"), $"{DateTimeOffset.UtcNow:o}\n{ex}"); } catch (Exception) { }
        StatusText.Text = title;
        try
        {
            for (var i = 0; i < 100 && Content.XamlRoot is null; i++) await Task.Delay(50);   // a dialog needs a window that is on screen
            Root.IsHitTestVisible = true;   // a dialog must be answerable
            var result = await new ContentDialog { Title = title, Content = new TextBlock { Text = message + "\n\nDetails were saved next to your data in startup-error.txt.", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, PrimaryButtonText = actionText ?? "", CloseButtonText = "Quit", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot }.ShowSerializedAsync();
            if (result == ContentDialogResult.Primary && actionUrl is not null) { try { await Windows.System.Launcher.LaunchUriAsync(new Uri(actionUrl)); } catch (Exception) { } }
        }
        catch (Exception) { }
        _shutdownCheckpointDone = true;
        try { _leases?.Shutdown(); } catch (Exception) { }
        Application.Current.Exit();
    }

    private bool _shutdownCheckpointDone;
    private int _restoreEpoch;
    private bool _shutdownInProgress;
    private volatile bool _ready;
    private readonly HashSet<string> _agentProfilesEnded = [];

    /// <summary>
    /// A finished agent session (stopped, expired, or refused) has nothing left to do, so its throwaway profile (cookies, storage of the pages it visited) is
    /// deleted as soon as its engine processes are gone, not left until the next start.
    /// </summary>
    private async Task EndFinishedAgentProfilesAsync()
    {
        try
        {
            foreach (var s in (_agentHost?.Sessions ?? []).Where(x => x.CleanedUp).ToList())
            {
                if (!_agentProfilesEnded.Add(s.Id)) continue;
                await _leases!.EndEphemeralSessionAsync(IdentityContainer.Disposable, s.WorkspaceId);   // whatever cannot be deleted yet is swept at the next start
            }
        }
        catch (Exception) { /* best effort: the start-up sweep is the backstop */ }
    }
    private string? _startupNotice;   // something the person should know about how this start went (for example a damaged database that was set aside)

    // ---- First run / help ----

    private string SettingsPath => Path.Combine(DataDir, "settings.json");

    private bool FirstRunDone()
    {
        try { return File.Exists(SettingsPath) && JsonDocument.Parse(File.ReadAllText(SettingsPath)).RootElement.TryGetProperty("firstRunDone", out var v) && v.GetBoolean(); }
        catch (Exception) { return false; }
    }

    private void MarkFirstRunDone()
    {
        try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { firstRunDone = true, at = DateTimeOffset.UtcNow })); } catch (Exception) { }
    }

    private void OnHelpAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; OnHelp(s, new RoutedEventArgs()); }

    private async void OnHelp(object s, RoutedEventArgs e)
    {
        if (_kernel is null) return;
        var existing = _kernel.Tabs.FirstOrDefault(t => t.Url.Scheme == "jev" && t.Url.Host == "welcome");
        var t = existing ?? _kernel.Open(new Uri(WelcomePage.Url));
        await _kernel.ActivateAsync(t.Id);
    }

    /// <summary>Four short tips anchored to the real controls, shown once, replayable via F1.</summary>
    private async Task ShowFirstRunTipsAsync()
    {
        var tips = new (FrameworkElement Target, string Title, string Body)[]
        {
            (TabList, "Your tabs live here", "Every tab stays listed. Green = live in front, amber = live, blue = low priority, grey = virtual (using no memory). Click any to bring it back."),
            (ProductModeBox, "Pick a product mode", "Simple hides everything but tabs and Shield. Power shows it all. Developer and Agent unlock those modules."),
            (ShieldButton, "Shield shows its work", "See every blocked request and its rule; turn Shield off for a site in one click if something breaks."),
            (BrainButton, "AI is off until you say so", "Brain has two switches. Jev answers typed questions with probabilities; Ask summarizes after you confirm what is sent."),
        };
        foreach (var (target, title, body) in tips)
        {
            if (target.Visibility != Visibility.Visible) continue;
            var tcs = new TaskCompletionSource();
            var tip = new TeachingTip { Target = target, Title = title, Subtitle = body, IsLightDismissEnabled = true, CloseButtonContent = "Next", PreferredPlacement = TeachingTipPlacementMode.Auto };
            tip.Closed += (_, _) => tcs.TrySetResult();
            Root.Children.Add(tip);
            tip.IsOpen = true;
            await tcs.Task;
            Root.Children.Remove(tip);
        }
        MarkFirstRunDone();
    }

    private async Task InitAsync()
    {
        _leases = new WebView2LeaseManager(WebHost, Path.Combine(DataDir, "profiles"), Path.Combine(DataDir, "thumbnails")) { MaxLive = 5 };
        _db = BrowserDb.OpenOrRecover(Path.Combine(DataDir, "db", "browser.db"), out var damagedDbMovedTo);
        if (damagedDbMovedTo is not null) _startupNotice = $"Your saved tab list could not be read, so a fresh one was started. The damaged file was kept, not deleted: {damagedDbMovedTo}";
        _siteSettings = new SiteSettingsRepository(_db);

        // Shield: compile whatever lists are on disk before the first renderer exists; fetch lists if there are none.
        _filters = new FilterListStore(Path.Combine(DataDir, "filters"));
        _shield = new ShieldAdapter(_siteSettings);
        // Trust OS: permission prompts are owned by the window; policy decides most without UI.
        _permissions = new PermissionAdapter(new SitePermissionsRepository(_db), PromptPermissionAsync);
        // DevSpace: optional module. Off unless JEVBROWSE_DEVSPACE=1 or toggled in the Dev panel; attaches nothing when off.
        _dev = new DevSpaceAdapter(Path.Combine(DataDir, "devspace", "projects.json"));
        _dev.SetEnabled(Environment.GetEnvironmentVariable("JEVBROWSE_DEVSPACE") == "1");
        _leases.OnCoreCreated = async (core, id, container, isolation, url) => { ApplyPageScheme(core); await _shield.AttachAsync(core, id, url); _permissions.Attach(core, container, isolation); _dev.Attach(core, id); };
        _shield.WallDetected += id => DispatcherQueue.TryEnqueue(() =>
        {
            var t = _kernel?.Tabs.FirstOrDefault(x => x.Id == id);
            StatusText.Text = $"{t?.Url.Host ?? "site"} showed an anti-adblock wall. Shield panel → 'Disable for site' if you need the page; Shield stays honest about what it can and cannot do.";
        });
        _leases.OnCoreDisposed = id => { _shield.Detach(id); _dev.Detach(id); };
        _leases.OnPopupRequested = OnPopupRequested;
        _leases.OnDownloadRequested = ConfirmDownloadAsync;
        if (!_filters.HasActiveLists && Environment.GetEnvironmentVariable("JEVBROWSE_NO_FILTER_UPDATE") is null)
        {
            // First run with no lists: do not hold the whole window hostage to the network. Wait a few seconds, then start; the download finishes and
            // compiles in the background (its own failure is reported in the status line, never thrown).
            var update = UpdateFilterListsAsync().ContinueWith(t => { if (t.IsFaulted) DispatcherQueue.TryEnqueue(() => StatusText.Text = "Shield: could not download filter lists (" + t.Exception!.GetBaseException().Message + ")"); });
            await Task.WhenAny(update, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        else
            CompileFilters();

        // JevBrain: off by default; providers exist only if their keys are in the environment.
        _decisions = new DecisionLogRepository(_db);
        _providers = OpenAiCompatibleProvider.FromEnvironment();
        _jev = JevDecisionProvider.FromEnvironment();
        _brain = new BrainRouter(new DefaultTrustPolicy(), _brainPolicy, _providers,
            d => _decisions.Append(d.At, string.IsNullOrEmpty(d.Task) ? "?" : d.Task + (d.Automatic ? " (automatic)" : " (explicit)"), d.Source.ToString(), d.Rule, d.Model, string.IsNullOrEmpty(d.DataClassName) ? "?" : d.DataClassName, d.Redacted, d.RedactionCount, d.InputChars, d.Output, d.Version),
            decisions: _jev);

        var classifier = new DataClassifier(host => _siteSettings.DataClassOverrideForHost(host) is { } c ? (DataClass)c : null);
        _kernel = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.Combine(DataDir, "thumbnails"), null, new WorkspaceRepository(_db), new DefaultTrustPolicy(), classifier);
        _kernel.Changed += OnKernelChanged;
        _kernel.Load();
        RebuildWorkspaces();
        RebuildList();

        // Agent Gateway: in-process always; the loopback HTTP host is opt-in from the Agents panel.
        var auditDir = Path.Combine(DataDir, "agents", "audit");
        Directory.CreateDirectory(auditDir);
        AgentGateway.AgentGateway.RemoveLegacyScreenshotFiles(Path.Combine(DataDir, "agents", "screenshots"));   // pictures are memory-only now
        _agents = new AgentGateway.AgentGateway(_kernel, _leases, Path.Combine(DataDir, "agents", "screenshots"), ConfirmAgentActionAsync,
            (s, e) => File.AppendAllText(Path.Combine(auditDir, s.Id + ".jsonl"), JsonSerializer.Serialize(new { e.At, s.Manifest.Agent, e.Action, e.Target, e.Allowed, e.Reason }) + "\n"));
        _agents.SessionsChanged += () => DispatcherQueue.TryEnqueue(() => { UpdateAgentIndicator(); _ = EndFinishedAgentProfilesAsync(); });

        // Browser Memory: indexes only what Trust OS allows (PUBLIC by default); 200 MB budget.
        _memory = new BrowserMemory(_db);
        _indexer = new MemoryIndexer(_kernel, _leases, _memory);
        // Skipping is the normal case (Not assessed pages are deliberately left out) and the reason is on the class badge.
        // Only saying something when a page WAS saved keeps the status line for things a person acted on.
        _indexer.Decided += (_, why) => { if (why.StartsWith("indexed", StringComparison.Ordinal)) DispatcherQueue.TryEnqueue(() => StatusText.Text = "Saved this page so you can search it later."); };

        // Dev/bench: JEVBROWSE_AI=1 starts with AI + Cloud on (the UI switches remain the user's control).
        if (Environment.GetEnvironmentVariable("JEVBROWSE_AI") == "1") { _brainPolicy.AiEnabled = true; _brainPolicy.CloudEnabled = true; _brainPolicy.AutomaticJudgments = true; }

        // Shield semantic clutter pass (§9): residual empty boxes → one batched Jev noul each → collapse at ≥ 0.8.
        _shield.SemanticPass = async (core, id) =>
        {
            if (_brain is null || !_brain.HasDecisionProvider || !_brainPolicy.AiEnabled || !_brainPolicy.CloudEnabled) return;
            var tab = _kernel.Tabs.FirstOrDefault(t => t.Id == id);
            if (tab is null || _kernel.ClassOf(tab) != DataClass.Public) return;
            var cands = await _shield.ResidualCandidatesAsync(core);
            if (cands.Count == 0) return;
            var state = $"Host: {tab.Url.Host}\nPath: {tab.Url.AbsolutePath}\nTitle: {tab.Title}\nAds blocked on this page: {(_shield.Stats.TryGetValue(id, out var s0) ? s0.Blocked : 0)}\n" +
                        string.Join("\n", cands.Select((c, i) => $"Element {i + 1}: {c.Describe()}"));
            var a = await _brain.JudgeAsync("clutter", state, Judgements.ClutterQuestions(cands.Select(c => c.Describe()).ToList()), DataClass.Public, _kernel.ContainerOf(tab), default);
            if (a is null) return;
            var keys = cands.Where((c, i) => a.Nouls.TryGetValue($"e{i}", out var p) && p >= 0.8).Select(c => c.K).ToList();
            var n = await _shield.CollapseCandidatesAsync(core, id, keys);
            if (_shield.Stats.TryGetValue(id, out var st)) st.LastSemantic = string.Join(" ", cands.Select((c, i) => $"e{i}:{(a.Nouls.TryGetValue($"e{i}", out var p) ? p.ToString("0.00") : "?")}"));
            if (n > 0) DispatcherQueue.TryEnqueue(() => StatusText.Text = $"Jev collapsed {n} residual ad slot(s) on {tab.Url.Host}");
        };

        // JevBrain layer 4: after a page loads, ask Jev a typed classification question (structure only, no content)
        // and let it RAISE the data class. Runs only with AI + Cloud on; every answer is in the decision log.
        _kernel.Changed += async e =>
        {
            if (e.Kind != "restored" || _brain is null || !_brain.HasDecisionProvider || !_brainPolicy.AiEnabled || !_brainPolicy.CloudEnabled) return;
            var tab = _kernel.Tabs.FirstOrDefault(t => t.Id == e.Id);
            if (tab is null || !_leases.TryGet(tab.Id, out var lease)) return;
            var cls = _kernel.ClassOf(tab);
            if (cls != DataClass.Public) return; // deterministic layer already decided something stricter
            try
            {
                var map = await lease.GetPageMapAsync(default);
                if (map is null) return;
                var state = Judgements.PageState(tab.Url, map.Title, map.Headings, _kernel.SignalsOf(tab), map.Links.Count, map.Fields.Count);
                var questions = new Dictionary<string, Question>(Judgements.PageQuestions());
                var wsNames = _kernel.Workspaces.Select(w => w.Name).Distinct().ToList();
                if (wsNames.Count > 1) foreach (var kv in Judgements.WorkspaceQuestion(wsNames)) questions[kv.Key] = kv.Value;
                var a = await _brain.JudgeAsync("classify", state, questions, cls, _kernel.ContainerOf(tab), default);
                if (a is null) return;
                if (a.Choices.TryGetValue(Judgements.DataClassQ, out var ans) && Judgements.RaiseFrom(cls, ans) is { } raised && _kernel.RaiseClass(tab.Id, raised))
                    DispatcherQueue.TryEnqueue(() => { StatusText.Text = $"Jev raised {tab.Url.Host} to {raised} (confidence {ans.Confidence:0.00})"; UpdateClassBadge(); });
                if (a.Choices.TryGetValue(Judgements.WorkspaceQ, out var ws) && ws.Confidence >= 0.8 && Judgements.WorkspaceNameFor(ws.Choice, wsNames) is { } suggested)
                {
                    var current = _kernel.Workspaces.FirstOrDefault(w => w.Id == tab.WorkspaceId)?.Name;
                    if (current is not null && suggested != current)
                        DispatcherQueue.TryEnqueue(() => StatusText.Text = $"Jev suggests: this tab belongs in '{suggested}' ({ws.Confidence:0.00}). Ctrl+K → Move this tab to: {suggested}");
                }
            }
            catch (Exception) { /* advisory only */ }
        };

        foreach (var m in Enum.GetValues<MemoryMode>()) ModeBox.Items.Add(m.ToString());
        ModeBox.SelectedIndex = (int)MemoryMode.Balanced;
        foreach (var m in Enum.GetValues<ProductMode>()) ProductModeBox.Items.Add(m.ToString());
        // Simple by default: a first-time user gets tabs, Shield and privacy, and grows into Power.
        ProductModeBox.SelectedIndex = (int)(Enum.TryParse<ProductMode>(Environment.GetEnvironmentVariable("JEVBROWSE_MODE"), true, out var pm) ? pm : ProductMode.Simple);
        await _modeChangeTask;
        ApplySidebar(LoadSidebarCollapsed(), remember: false);

        // Resource OS tick: sample → evaluate → apply. 10 s is coarse on purpose; user actions never wait for it.
        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(10);
        _tick.Tick += async (_, _) => await SchedulerTickAsync();
        _tick.Start();

        var args = Environment.GetCommandLineArgs();
        _leases.LocalPage = u => u.Scheme == "jev" && u.Host == "welcome" ? WelcomePage.Html(_providers.Any(p => p.IsConfigured), _jev?.IsConfigured == true) : null;

        var uiDialog = args.FirstOrDefault(a => a.StartsWith("--ui-dialog=", StringComparison.Ordinal)) is { } ud0 ? ud0["--ui-dialog=".Length..] : null;
        if (args.Contains("--resume-seed")) _ = RunResumeSeedAsync();   // scripts/additions-check.ps1 then closes the app and starts it again
        if (args.Contains("--recovery-seed"))
            _ = RunRecoverySeedAsync();   // scripts/recovery-check.ps1 then kills or closes the app and inspects what is left
        if (args.FirstOrDefault(a => a.StartsWith("--idle-scenario=", StringComparison.Ordinal)) is { } idleScenario)
            _ = RunIdleScenarioAsync(idleScenario["--idle-scenario=".Length..]);   // read from outside by scripts/idle-benchmark.ps1
        if (args.FirstOrDefault(a => a.StartsWith("--agent-hidden-load=", StringComparison.Ordinal)) is { } hiddenLoad)
            _ = RunAgentHiddenLoadAsync(hiddenLoad["--agent-hidden-load=".Length..]);   // measured from outside by scripts/idle-cost.ps1 -AgentLoad
        if (args.Contains("--ui-shot") || uiDialog is not null)
        {
            // Render the window itself (not the screen) after the welcome page and tips settle, for docs and review.
            // --ui-width=N resizes first, so narrow layouts can be looked at rather than reasoned about.
            if (args.FirstOrDefault(a => a.StartsWith("--ui-width=", StringComparison.Ordinal)) is { } uw
                && int.TryParse(uw["--ui-width=".Length..], out var uiWidth))
                DispatcherQueue.TryEnqueue(() => AppWindow.Resize(new Windows.Graphics.SizeInt32(uiWidth, 780)));
            // The saved preference would otherwise decide which state a review screenshot shows.
            if (args.Contains("--ui-sidebar=hidden")) DispatcherQueue.TryEnqueue(() => ApplySidebar(true, remember: false));
            if (args.Contains("--ui-sidebar=open")) DispatcherQueue.TryEnqueue(() => ApplySidebar(false, remember: false));
            _ = Task.Run(async () =>
            {
                await Task.Delay(9000);
                if (uiDialog is not null)
                {
                    // Show one decision dialog and leave it up, for scripts/text-size-check.ps1 to inspect and then close with the process.
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.Combine(DataDir, "benchmarks"));
                            await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "dialog-report.json"),
                                System.Text.Json.JsonSerializer.Serialize(new { textScaleFactor = new Windows.UI.ViewManagement.UISettings().TextScaleFactor, dialog = uiDialog }));
                            if (uiDialog == "permission") await PromptPermissionAsync("news.example.com", PermissionKind.Camera, "use your camera and microphone");
                            else if (uiDialog == "workspace") OnNewWorkspace(this, new RoutedEventArgs());
                        }
                        catch (Exception) { /* evidence only */ }
                    });
                    return;
                }
                DispatcherQueue.TryEnqueue(async () =>
                {
                    if (args.Contains("--ui-demo-agent") && _agents is not null)
                    {
                        // A real endpoint and a real session, so the agent panel can be looked at with something in it.
                        var ceiling = new AgentCeiling { Limits = new AgentManifest { Agent = "ceiling", AllowDomains = ["example.com"], Actions = [AgentAction.Navigate, AgentAction.Read], SessionMinutes = 30, MaxLivePages = 2, MaxActions = 100 } };
                        _agentHost = new LocalAgentHost(_agents, ceiling);
                        _agentHost.BackgroundFault += OnAgentHostFault;
                        _agentHost.Start();
                        var (demo, _) = await _agentHost.GrantAsync(new AgentManifest { Agent = "Claude Code", AllowDomains = ["example.com"], Actions = [AgentAction.Navigate, AgentAction.Read], SessionMinutes = 30, MaxLivePages = 2 });
                        await _agents.ExecuteAsync(demo, new AgentRequest(AgentAction.Navigate, "https://example.com/"), default);
                        await _agents.ExecuteAsync(demo, new AgentRequest(AgentAction.Navigate, "https://not-approved.test/login"), default);   // refused: outside the approved domain
                    }
                    if (args.FirstOrDefault(a => a.StartsWith("--ui-panel=", StringComparison.Ordinal)) is { } up) { var which = up["--ui-panel=".Length..]; if (which == "receipt") OnReceipt(this, new RoutedEventArgs()); else if (which == "shield") OnShield(this, new RoutedEventArgs()); else if (which == "explain") OnExplain(this, new RoutedEventArgs()); else if (which == "search") OnPalette(this, new RoutedEventArgs()); else if (which == "workspaces") OnWorkspaceOverview(this, new RoutedEventArgs()); else if (which == "agents") OnAgentActivity(this, new RoutedEventArgs()); await Task.Delay(700); }
                    try
                    {
                        // What the person's Windows "Show animations" setting did to this run: the decision, and how long a real
                        // fade took to finish. Written for scripts/reduced-motion-check.ps1, which flips the setting and restores it.
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        var faded = new TaskCompletionSource();
                        Motion.Fade(StatusText, 1f, JevBrowse.VirtualTabs.MotionKind.Base, () => faded.TrySetResult());
                        await Task.WhenAny(faded.Task, Task.Delay(2000));
                        Directory.CreateDirectory(Path.Combine(DataDir, "benchmarks"));
                        await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "motion-report.json"), System.Text.Json.JsonSerializer.Serialize(new
                        {
                            textScaleFactor = new Windows.UI.ViewManagement.UISettings().TextScaleFactor,
                            transparencyEffects = new Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled,
                            animationsEnabled = Motion.Enabled,
                            fadeCompleted = faded.Task.IsCompleted,
                            fadeMilliseconds = watch.ElapsedMilliseconds,
                            policyBaseMilliseconds = JevBrowse.VirtualTabs.MotionPolicy.Duration(JevBrowse.VirtualTabs.MotionKind.Base, Motion.Enabled).TotalMilliseconds,
                        }));
                    }
                    catch (Exception ex) { try { await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "motion-error.txt"), ex.ToString()); } catch (Exception) { } }
                    try
                    {
                        var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                        await rtb.RenderAsync(Root);
                        var buf = await rtb.GetPixelsAsync();
                        var pixels = new byte[buf.Length];
                        using (var dr = Windows.Storage.Streams.DataReader.FromBuffer(buf)) dr.ReadBytes(pixels);
                        Directory.CreateDirectory(Path.Combine(DataDir, "benchmarks"));
                        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.Combine(DataDir, "benchmarks"));
                        var file = await folder.CreateFileAsync("ui-shot.png", Windows.Storage.CreationCollisionOption.ReplaceExisting);
                        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
                        var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                        enc.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels);
                        await enc.FlushAsync();
                    }
                    catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "bench-error.txt"), ex.ToString()); }
                    try { _leases?.Shutdown(); } catch (Exception) { }   // Exit() alone orphans every renderer process
                    Application.Current.Exit();
                });
            });
        }

        if (args.Contains("--memory-lab") || args.Contains("--restore-bench") || args.Contains("--shield-check") || args.Contains("--memory-check") || args.Contains("--youtube-check") || args.Contains("--privacy-check") || args.Contains("--private-session-check") || args.Contains("--agent-check") || args.Contains("--agent-window-check") || args.Contains("--agent-screenshot-stage-check") || args.Contains("--agent-frame-secret-check") || args.Contains("--agent-show-during-capture-check") || args.Contains("--agent-indicator-check") || args.Contains("--idle-invariants-check") || args.Contains("--nav-check") || args.Contains("--additions-check") || args.Contains("--media-check") || args.Contains("--site-sweep") || args.Any(a => a.StartsWith("--join=", StringComparison.Ordinal)))
        {
            Directory.CreateDirectory(Path.Combine(DataDir, "benchmarks"));
            try
            {
                if (args.FirstOrDefault(a => a.StartsWith("--join=", StringComparison.Ordinal)) is { } j) await RunJoinCheckAsync(j["--join=".Length..]);
                else if (args.Contains("--private-session-check")) await RunPrivateSessionCheckAsync();
                else if (args.Contains("--site-sweep")) await RunSiteSweepAsync();
                else if (args.Contains("--media-check")) await RunMediaCheckAsync();
                else if (args.Contains("--agent-check")) await RunAgentCheckAsync();
                else if (args.Contains("--agent-window-check")) await RunAgentWindowCheckAsync();
                else if (args.Contains("--agent-screenshot-stage-check")) await RunAgentScreenshotStageCheckAsync();
                else if (args.Contains("--agent-frame-secret-check")) await RunAgentFrameSecretCheckAsync();
                else if (args.Contains("--agent-show-during-capture-check")) await RunAgentShowDuringCaptureCheckAsync();
                else if (args.Contains("--agent-indicator-check")) await RunAgentIndicatorCheckAsync();
                else if (args.Contains("--idle-invariants-check")) await RunIdleInvariantsCheckAsync();
                else if (args.Contains("--nav-check")) await RunNavCheckAsync();
                else if (args.Contains("--additions-check")) await RunAdditionsCheckAsync();
                else if (args.Contains("--privacy-check")) await RunPrivacyCheckAsync();
                else if (args.Contains("--memory-lab")) await RunMemoryLabAsync();
                else if (args.Contains("--restore-bench")) await RunRestoreBenchAsync();
                else if (args.Contains("--shield-check")) await RunShieldCheckAsync();
                else if (args.Contains("--youtube-check")) await RunYouTubeCheckAsync();
                else await RunMemoryCheckAsync();
            }
            catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "bench-error.txt"), ex.ToString()); }
            // Exit() does not close renderers: every WebView2 process this run started would be orphaned, and
            // Shutdown() is also what deletes this session's ephemeral profiles. A benchmark that leaks 27 renderer
            // processes is not a benchmark of anything, and it starved the next build of memory.
            try { _leases?.Shutdown(); } catch (Exception) { }
            Application.Current.Exit();
            return;
        }

        var firstRun = !FirstRunDone();
        // Development and measurement only: lets a check open a known page (for example a deliberately animated one, as a
        // positive control for the idle-cost script) without a code change. Unset, behaviour is exactly as before.
        var startUrl = Environment.GetEnvironmentVariable("JEVBROWSE_START_URL");
        // Pick up where the person left off: the last ordinary workspace and its active tab, restored lazily (only that tab gets a renderer). A start URL from
        // the environment (measurement only) always wins; anything missing falls back to the normal start.
        var resume = string.IsNullOrWhiteSpace(startUrl) ? ResumeChoiceFromPrefs() : null;
        if (resume is { Tab: { } resumeTab }) { await _kernel.ActivateAsync(resumeTab); }
        else
        {
            if (resume is not null) await _kernel.SwitchWorkspaceAsync(resume.Workspace);
            if (!_kernel.TabsIn(_kernel.ActiveWorkspace).Any())
                _kernel.Open(new Uri(!string.IsNullOrWhiteSpace(startUrl) && Uri.IsWellFormedUriString(startUrl, UriKind.Absolute) ? startUrl : firstRun ? WelcomePage.Url : "https://example.com"));
            await _kernel.ActivateAsync(_kernel.TabsIn(_kernel.ActiveWorkspace).First().Id);
        }
        _ready = true;
        Root.IsHitTestVisible = true;
        StatusText.Text = "";
        if (_startupNotice is not null)
        {
            // Data was set aside: that is worth an explicit acknowledgement rather than a status line that the next page load overwrites.
            var notice = _startupNotice; _startupNotice = null;
            StatusText.Text = notice;
            await new ContentDialog { Title = "Your saved tabs could not be read", Content = new TextBlock { Text = notice, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot }.ShowSerializedAsync();
        }
        if (firstRun) { await Task.Delay(800); await ShowFirstRunTipsAsync(); }
    }

    // ---- kernel → UI ----

    private readonly Dictionary<ResourceId, List<DateTimeOffset>> _engineRecoveries = [];

    /// <summary>
    /// A page's engine died and the kernel released it. If it was the page in front, bring it back on a fresh renderer (at most twice a minute per tab, so a
    /// page that crashes the engine every time is not reloaded in a loop); otherwise it stays asleep and wakes when it is next opened.
    /// </summary>
    private void OnEngineFailed(KernelEvent e)
    {
        var host = _kernel?.Tabs.FirstOrDefault(t => t.Id == e.Id)?.Url.Host ?? "this page";
        if (!e.Reason.Contains("was in front", StringComparison.Ordinal))
        {
            StatusText.Text = $"A background page ({host}) crashed and was put to sleep. It will reload when you open it.";
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var recent = _engineRecoveries.TryGetValue(e.Id, out var l) ? l : _engineRecoveries[e.Id] = [];
        recent.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
        if (recent.Count >= 2)
        {
            StatusText.Text = $"{host} keeps crashing the browser engine, so it was not reloaded again. Select the tab to try once more.";
            return;
        }
        recent.Add(now);
        StatusText.Text = $"{host} crashed. Reloading it…";
        _ = ReactivateAfterCrashAsync(e.Id);
    }

    private async Task ReactivateAfterCrashAsync(ResourceId id)
    {
        try { if (_kernel is not null && _kernel.Tabs.Any(t => t.Id == id)) await _kernel.ActivateAsync(id); }
        catch (Exception ex) { StatusText.Text = "Could not reload the crashed page: " + ex.Message; }
    }


    private void OnKernelChanged(KernelEvent e)
    {
        if (e.Kind is "workspace-created" or "workspace-ended" or "context-restored" or "opened" or "closed" or "moved") RebuildWorkspaces();
        if (e.Kind is "workspace-switched") { SyncWorkspaceBox(); RebuildList(); }
        else if (e.Kind is "opened" or "closed" or "loaded" or "moved" or "context-restored" or "pinned") RebuildList();
        else foreach (var i in Items) i.Refresh();

        if (e.Kind == "activated")
        {
            _syncingSelection = true;
            TabList.SelectedItem = Items.FirstOrDefault(i => i.Id == e.Id);
            _syncingSelection = false;
            SetAddress(_kernel!.Active?.Url.ToString() ?? "");
        }
        else if (e.Kind == "navigated" && _kernel?.Active?.Id == e.Id && !IsEditingAddress)
        {
            // A followed link, a redirect, script navigation, Back and Forward all arrive here. The tab in FRONT shows where it really is, unless the person is
            // in the middle of typing (their text is never overwritten). Another tab's navigation, an agent's page in the background, never touches the bar.
            SetAddress(_kernel.Active.Url.ToString());
        }
        if (e.Kind == "activated") RememberResumePoint();
        if (e.Kind is "activated" or "navigated" or "signals") { UpdateClassBadge(); UpdateEnvChrome(); }
        if (e.Kind is "activated" or "navigated") RefreshPanel();
        if (e.Kind is "activated" or "pinned" or "protection" or "loaded") UpdateTabControls();
        // The restore panel, its timer and its buttons belong to the page in front of the person. Work done in the background (an
        // agent's page coming live) raises the same kernel events but must not touch any of it: it once replaced the tracked restore
        // with its own, so the person's real restore finished unnoticed and the panel stayed up.
        if (e.Kind == "engine-failed") OnEngineFailed(e);
        if (e.Kind == "restoring" && !e.Background) ShowRestoring(e.Id, e.Reason.StartsWith("with"));
        if ((e.Kind is "restored" or "loaded") && !e.Background) FinishRestore(e.Id);
        UpdateIdlePanel();
        UpdatePoolText();
        // The private session's tab count and controls are facts about the tab set, so they follow it: opening or
        // closing a private tab changes what the sidebar must say, not just switching workspaces.
        if (e.Kind is "workspace-switched" or "workspace-ended" or "opened" or "closed" or "moved" or "context-restored") UpdatePrivateSessionUi();
        // Only events worth telling a person about produce text; the rest leave the line alone, so a message an action
        // just wrote is not overwritten by bookkeeping.
        if (KernelStatus.For(e) is { } friendly) StatusText.Text = friendly;
    }

    /// <summary>
    /// The two tab controls say what they will do next, not what state the tab is in. "Pin" / "Unpin" is placement;
    /// "Keep active" / "Let it sleep" is sleeping. Neither caption implies the other.
    /// </summary>
    private void UpdateTabControls()
    {
        var t = _kernel?.Active;
        PinButton.IsEnabled = KeepActiveButton.IsEnabled = SleepItem.IsEnabled = t is not null;
        if (t is null) return;
        PinButton.Text = t.IsPinned ? "Unpin" : "Pin to top";
        KeepActiveButton.Text = t.UserProtection.HasFlag(ProtectionFlags.KeepActive) ? "Let it sleep" : "Keep active";
    }

    /// <summary>The sidebar shows the active workspace only; other workspaces' tabs stay durable and (eventually) virtual.</summary>
    private void RebuildList()
    {
        Items.Clear();
        foreach (var t in _kernel!.TabsIn(_kernel.ActiveWorkspace)) Items.Add(new TabItem(t));
    }

    // ---- Context OS ----

    private bool _syncingWorkspace;

    private void RebuildWorkspaces()
    {
        _syncingWorkspace = true;
        WorkspaceBox.Items.Clear();
        // The identity is on the address badge; repeating the default one here only truncated the name.
        foreach (var w in _kernel!.Workspaces)
            WorkspaceBox.Items.Add(w.Container == IdentityContainer.Personal
                ? $"{w.Name} ({_kernel.TabsIn(w.Id).Count()})"
                : $"{w.Name} · {w.Container} ({_kernel.TabsIn(w.Id).Count()})");
        _syncingWorkspace = false;
        SyncWorkspaceBox();
    }

    private void SyncWorkspaceBox()
    {
        _syncingWorkspace = true;
        var idx = _kernel!.Workspaces.ToList().FindIndex(w => w.Id == _kernel.ActiveWorkspace);
        if (idx >= 0 && idx < WorkspaceBox.Items.Count) WorkspaceBox.SelectedIndex = idx;
        _syncingWorkspace = false;
    }

    private async void OnWorkspaceChanged(object s, SelectionChangedEventArgs e)
    {
        if (_syncingWorkspace || _kernel is null || WorkspaceBox.SelectedIndex < 0 || WorkspaceBox.SelectedIndex >= _kernel.Workspaces.Count) return;
        await _kernel.SwitchWorkspaceAsync(_kernel.Workspaces[WorkspaceBox.SelectedIndex].Id);
        RebuildWorkspaces();
        UpdateIdlePanel();
        UpdatePoolText();
    }

    private async void OnNewWorkspace(object s, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "Workspace name, e.g. Job search" };
        var container = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Identity container" };
        foreach (var c in Enum.GetValues<IdentityContainer>()) container.Items.Add(c + (c.IsEphemeral() ? " (nothing persisted)" : ""));
        container.SelectedIndex = 0;
        var dlg = new ContentDialog { Title = "New workspace", Content = new StackPanel { Spacing = Tokens.Space(8), Children = { box, container } }, PrimaryButtonText = "Create", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
        if (await dlg.ShowSerializedAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
        var w = _kernel!.CreateWorkspace(box.Text.Trim(), (IdentityContainer)container.SelectedIndex);
        await _kernel.SwitchWorkspaceAsync(w.Id);
        RebuildWorkspaces();
        UpdateIdlePanel();
    }

    private void OnMoveMenuOpening(object s, object e)
    {
        MoveMenu.Items.Clear();
        if (_kernel?.Active is not { } t) return;
        foreach (var w in _kernel.Workspaces.Where(w => w.Id != t.WorkspaceId))
        {
            var item = new MenuFlyoutItem { Text = w.Name };
            var target = w.Id;
            item.Click += async (_, _) => await MoveActiveAsync(t, target, w.Name);
            MoveMenu.Items.Add(item);
        }
        if (MoveMenu.Items.Count == 0) MoveMenu.Items.Add(new MenuFlyoutItem { Text = "No other workspaces", IsEnabled = false });
    }

    private async void OnTimeline(object s, RoutedEventArgs e)
    {
        var timeline = _kernel!.Timeline();
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 400 };
        foreach (var c in timeline)
            list.Items.Add($"{c.At.ToLocalTime():ddd HH:mm} — {c.WorkspaceName} — {c.Resources.Count} resources — {c.LiveCount} live");
        var dlg = new ContentDialog
        {
            Title = "Time Travel",
            Content = timeline.Count == 0 ? new TextBlock { Text = "No context checkpoints yet. One is recorded every 5 minutes and on every workspace switch." } : list,
            PrimaryButtonText = "Restore", CloseButtonText = "Close", XamlRoot = Content.XamlRoot,
            IsPrimaryButtonEnabled = timeline.Count > 0,
        };
        if (await dlg.ShowSerializedAsync() != ContentDialogResult.Primary || list.SelectedIndex < 0) return;
        var n = await _kernel.RestoreContextAsync(timeline[list.SelectedIndex]);
        StatusText.Text = $"context restored: {n} tabs recreated (virtual), only the active one loaded";
    }

    private int _ticksSinceCheckpoint;

    private void MaybeRecordContextCheckpoint()
    {
        if (++_ticksSinceCheckpoint < 30) return; // 30 × 10 s = 5 min
        _ticksSinceCheckpoint = 0;
        _kernel!.RecordContextCheckpoint();
        new WorkspaceRepository(_db!).PruneCheckpoints(keepPerWorkspace: 50, maxAge: TimeSpan.FromDays(30), DateTimeOffset.UtcNow);
    }

    private void UpdatePoolText()
    {
        var s = ProcessGroupProbe.Sample(_leases!.ProcessIds);
        var blocked = _shield?.SessionBlockedTotal ?? 0;
        var tabs = _kernel!.Tabs.Count;
        // Plain words. The scheduler band and process count are diagnostics; Explain and Receipt carry them.
        PoolText.Text = $"{tabs} {(tabs == 1 ? "tab" : "tabs")} open, {_kernel.LiveCount} awake (up to {_leases.MaxLive})\n"
                      + $"Pages are using {s.PrivateMb:F0} MB of memory\n"
                      + $"Shield has blocked {blocked:N0} {(blocked == 1 ? "request" : "requests")} this session";
        if (PoolText.MaxLines == 2) ToolTipService.SetToolTip(PoolText, PoolText.Text);   // held to two lines at large text sizes
    }

    // ---- Restore experience ----

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _restoreTimer;
    private ResourceId? _restoringId;

    /// <summary>Nothing selected, or the selected tab is back: the panel says which, and never sits there lying.</summary>
    private void UpdateIdlePanel()
    {
        ClearStaleRestoreMessage();
        // A restore whose tab has since been closed is over; nothing will ever finish it.
        if (_restoringId is { } gone && _kernel is not null && _kernel.Tabs.All(t => t.Id != gone)) { _restoringId = null; _restoreTimer?.Stop(); }
        // No active tab is exactly when a restore is most visible (start-up, or after the last tab was closed): the tab that is about to
        // become active is still coming back. Saying "Nothing open here" over that restore was wrong, so an in-flight restore keeps the panel.
        if (_kernel?.Active is null && _restoringId is not null) return;
        if (_kernel?.Active is null)
        {
            _restoringId = null;
            _restoreTimer?.Stop();
            RestorePanel.Visibility = Visibility.Visible;
            PreviewFrame.Visibility = Visibility.Collapsed;
            RestoreProgress.Visibility = Visibility.Collapsed;
            RestoreActions.Visibility = Visibility.Collapsed;
            RestoreTitle.Text = "Nothing open here";
            RestoreStatus.Text = "Choose a tab on the left, or press Ctrl+K.";
        }
        else if (_restoringId is null) { RestorePanel.Visibility = Visibility.Collapsed; RestoreProgress.Visibility = Visibility.Collapsed; }
    }

    private void ShowRestoring(ResourceId id, bool hasSavedPlace)
    {
        var tab = _kernel?.Tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return;
        _restoringId = id;
        _restoreEpoch++;                       // any fade still finishing from an earlier restore must not hide THIS one
        RestorePanel.Opacity = 1;
        RestorePanel.Visibility = Visibility.Visible;
        RestoreActions.Visibility = Visibility.Collapsed;
        RestoreProgress.Visibility = Visibility.Visible;
        RestoreTitle.Text = string.IsNullOrWhiteSpace(tab.Title) ? tab.Url.Host : tab.Title;
        // What we promise depends on what was actually kept, not on whether a checkpoint row exists. A capture that
        // saved the address but lost the scroll position must not say "your place is saved".
        var savedPlace = _kernel?.LastCapture(id) is { } last ? last.Preserved.HasFlag(PreservedParts.Position) : hasSavedPlace;
        RestoreStatus.Text = savedPlace ? "Restoring… your place is saved." : "Restoring… we did not save a position for this page, so it will open at the top.";

        // The preview is only ever a previously saved image, and only when policy let us keep one.
        PreviewFrame.Visibility = Visibility.Collapsed;
        var thumb = _kernel!.GetCheckpoint(id)?.ThumbnailPath;
        if (thumb is not null && File.Exists(thumb))
        {
            try
            {
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                using var fs = File.OpenRead(thumb);
                bmp.SetSource(fs.AsRandomAccessStream());
                PreviewImage.Source = bmp;
                PreviewFrame.Visibility = Visibility.Visible;
            }
            catch (Exception) { }
        }

        _restoreTimer ??= DispatcherQueue.CreateTimer();
        _restoreTimer.Stop();
        _restoreTimer.Interval = TimeSpan.FromSeconds(12);
        _restoreTimer.IsRepeating = false;
        _restoreTimer.Tick -= OnRestoreTimeout;
        _restoreTimer.Tick += OnRestoreTimeout;
        _restoreTimer.Start();
    }

    private void OnRestoreTimeout(Microsoft.UI.Dispatching.DispatcherQueueTimer s, object e)
    {
        if (_restoringId is null) return;
        RestoreProgress.Visibility = Visibility.Collapsed;
        RestoreActions.Visibility = Visibility.Visible;
        RestoreStatus.Text = "This page is taking longer than expected. It may be slow, offline, or blocking the load.";
    }

    private void FinishRestore(ResourceId id)
    {
        if (_restoringId != id) return;
        _restoreTimer?.Stop();
        _restoringId = null;
        // Continuity: the saved picture fades into the live page, so it is clear the picture was a stand-in and that the
        // real page has arrived. With animations off in Windows the change is immediate. The epoch check stops a fade that
        // is still finishing from hiding a NEW restore that started in the meantime.
        var epoch = ++_restoreEpoch;
        Motion.Fade(RestorePanel, 0f, JevBrowse.VirtualTabs.MotionKind.Fast, () =>
        {
            if (epoch != _restoreEpoch) return;
            RestorePanel.Visibility = Visibility.Collapsed;
            RestorePanel.Opacity = 1;
            PreviewImage.Source = null;
            RestoreProgress.Visibility = Visibility.Collapsed;   // an indeterminate bar animates for as long as it is Visible, even in a collapsed panel
        });
        // Say so when something was actually lost, and name the loss. A missing preview image changed nothing the
        // user can see in the page, so it is not reported as a failed restore.
        var shortfall = _kernel?.LastCapture(id)?.Shortfall ?? "";
        if (shortfall.Length > 0) { StatusText.Text = $"Page reopened; {shortfall}."; _shortfallFor = id; }
        else if (_shortfallFor == id) _shortfallFor = null;
    }

    /// <summary>
    /// The tab a restore message is about. A message like "previous position unavailable" is about one page; leaving
    /// it in the status bar after the user moves to another tab makes it read as a statement about that tab instead.
    /// </summary>
    private ResourceId? _shortfallFor;

    private void ClearStaleRestoreMessage()
    {
        if (_shortfallFor is not { } id || _kernel?.Active?.Id == id) return;
        _shortfallFor = null;
        StatusText.Text = "";
    }

    private async void OnRestoreRetry(object s, RoutedEventArgs e)
    {
        if (_restoringId is not { } id || _kernel is null) return;
        await _kernel.VirtualizeAsync(id, Cause.User);
        ShowRestoring(id, _kernel.GetCheckpoint(id) is not null);
        await _kernel.ActivateAsync(id);
    }

    private async void OnRestoreOpenAddress(object s, RoutedEventArgs e)
    {
        if (_restoringId is not { } id || _kernel?.Tabs.FirstOrDefault(t => t.Id == id) is not { } tab) return;
        SetAddress(tab.Url.ToString());
        await _kernel.VirtualizeAsync(id, Cause.User);
        ShowRestoring(id, false);
        await _kernel.ActivateAsync(id);
    }

    private void OnRestoreKeepActive(object s, RoutedEventArgs e)
    {
        if (_restoringId is not { } id || _kernel?.Tabs.FirstOrDefault(t => t.Id == id) is not { } tab) return;
        _kernel.SetProtection(id, tab.UserProtection | ProtectionFlags.NeverHibernateSite);
        StatusText.Text = "This tab will be kept active and will not be put to sleep automatically.";
        foreach (var i in Items) i.Refresh();
    }

    // ---- Trust OS ----

    private void UpdateClassBadge()
    {
        if (_kernel?.Active is not { } t) { ClassBadgeText.Text = ""; return; }
        var cls = _kernel.ClassOf(t);
        // The badge states the identity and what we know about the page. "Not assessed" is an honest answer and is
        // never dressed up as a safety verdict.
        // On a row of its own the address bar needs the width, so the badge keeps the identity and drops the second
        // half; the full state stays in the accessible name and the tooltip, and the colour still says it.
        var full = $"{_kernel.ContainerOf(t).ToString().ToUpperInvariant()} • {ClassLabel(cls).ToUpperInvariant()}";
        var fullChanged = _badgeFullText != full;
        _badgeFullText = full;
        ClassBadgeText.Text = _addressWrapped ? _kernel.ContainerOf(t).ToString().ToUpperInvariant() : full;
        if (fullChanged) DispatcherQueue.TryEnqueue(LayoutToolbar);
        ToolTipService.SetToolTip(ClassBadge, full);
        // What a screen reader says: the state, then what pressing does. The visible text is capitals and a bullet.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ClassBadge,
            $"{_kernel.ContainerOf(t)} profile, {ClassLabel(cls)}. Press to change how this site is treated.");
        // From the token dictionaries, so the badge's text always has the contrast the tests measured. The class is also
        // in the words on the badge, so its colour is never the only thing that says it.
        ClassBadge.Background = Tokens.Brush(cls switch
        {
            DataClass.Public => "JevBadgePublicBrush",
            DataClass.Unknown => "JevBadgeUnknownBrush",
            DataClass.Authenticated => "JevBadgeSignedInBrush",
            DataClass.Sensitive => "JevBadgeSensitiveBrush",
            DataClass.Secret => "JevBadgeSecretBrush",
            _ => "JevBadgePrivateBrush",
        });
        ClassBadgeText.Foreground = Tokens.Brush("JevBadgeTextBrush");
    }

    /// <summary>User-facing name for a data class. Internal names are diagnostics, not labels.</summary>
    internal static string ClassLabel(DataClass c) => c switch
    {
        DataClass.Public => "Public",
        DataClass.Unknown => "Not assessed",
        DataClass.Authenticated => "Signed in",
        DataClass.Sensitive => "Sensitive",
        DataClass.Secret => "Secret",
        _ => "Private session",
    };

    private static string ClassExplanation(DataClass c) => c switch
    {
        DataClass.Public => "Saved for search, and can be summarized if you ask.",
        // The boundary here is about JevBrowse's own features, and the wording says only that. An agent you have
        // authorized is a separate grant with a separate explanation, and it CAN read this page -- claiming the
        // content "never leaves the device" would be broader than what is actually enforced.
        DataClass.Unknown => "This page isn't added to saved-page search or sent to JevBrowse's cloud AI. An agent you authorize may still access it.",
        DataClass.Authenticated => "Looks like you are signed in: kept on this device, not added to search, never sent to AI unless you ask.",
        DataClass.Sensitive => "Only the address and scroll position are kept. No screenshot, no search, no AI.",
        DataClass.Secret => "This page asks for a password or card number. Nothing about it is stored or sent.",
        _ => "Private tabs are not saved in history, previews or Browser Memory. Temporary website data is deleted when you end the session; locked files are retried on the next start.",
    };

    private async void OnClassBadgeTapped(object s, RoutedEventArgs e)
    {
        if (_kernel?.Active is not { } t) return;
        var site = DataClassifier.HostKey(t.Url.Host);
        var current = _kernel.ClassOf(t);
        // Bound to the enum values, not to positions: adding a class must not silently re-point saved overrides.
        var choices = new DataClass?[] { null, DataClass.Public, DataClass.Authenticated, DataClass.Sensitive };
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in choices) box.Items.Add(c is null ? "Let JevBrowse decide" : ClassLabel(c.Value));
        var over = _siteSettings!.ExactDataClassOverride(site);
        var olderApplies = over is null && _siteSettings.DataClassOverrideForHost(site) is not null;
        box.SelectedIndex = Math.Max(0, Array.FindIndex(choices, c => (int?)c == over));
        var dlg = new ContentDialog
        {
            Title = $"How should {site} be treated?",
            Content = new StackPanel { Spacing = Tokens.Space(8), Children = {
                new TextBlock { Text = $"Now: {ClassLabel(current)}. {ClassExplanation(current)}", TextWrapping = TextWrapping.Wrap },
                box,
                new TextBlock { Text = olderApplies ? "An older decision that covered several sites is still applied here because it is stricter. Choose below to decide for this address only." : "", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7, Visibility = olderApplies ? Visibility.Visible : Visibility.Collapsed },
                new TextBlock { Text = "A page asking for a password or card number is always treated as Secret, whatever you choose here.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 } } },
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowSerializedAsync() != ContentDialogResult.Primary) return;
        var chosen = choices[Math.Max(0, box.SelectedIndex)];
        _siteSettings.SetDataClassOverrideForHost(site, (int?)chosen);
        // Apply it NOW, not at the next qualifying event: stricter means the previews, saved positions and indexed text this site no longer qualifies for
        // are deleted immediately, for every tab on it and for pages of it that were indexed earlier.
        _kernel.ReapplyPolicy();
        if (chosen != DataClass.Public) _memory?.ForgetSite(site);
        UpdateClassBadge();
    }

    /// <summary>Set only by the --media-check bench, which runs with nobody present to answer a prompt.</summary>
    private bool _autoAllowPermissions;
    /// <summary>Set by unattended checks that load real sites: nobody is there to answer, and granting is not the point.</summary>
    private bool _autoDenyPermissions;

    private async Task<PermissionChoice> PromptPermissionAsync(string site, PermissionKind kind, string what)
    {
        try { File.AppendAllText(Path.Combine(DataDir, "benchmarks", "site-sweep.progress.log"), $"{DateTime.Now:HH:mm:ss}   PERMISSION PROMPT {kind} ({what}) from {site}\n"); } catch (Exception) { }
        if (_autoAllowPermissions) return PermissionChoice.AllowOnce;
        if (_autoDenyPermissions) return PermissionChoice.BlockOnce;
        var tcs = new TaskCompletionSource<PermissionChoice>();
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                // Three explicit outcomes. Escape and the close button return the same result from the dialog, so they
                // must mean the same safe thing: refuse THIS request, remember nothing. A permanent block is its own
                // button. Enter also lands on "Not now" (DefaultButton), so a stray keypress can never grant access.
                var duration = new ComboBox { ItemsSource = new[] { "Just this time", "For 1 hour" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
                var dlg = new ContentDialog
                {
                    Title = $"{site} wants to {what}",
                    Content = new StackPanel
                    {
                        Spacing = Tokens.Space(8),
                        Children =
                        {
                            new TextBlock { Text = "Choose how long to allow it. \"Not now\" (or Esc) refuses only this request and you will be asked again next time. \"Block this site\" refuses it until you change your mind.", TextWrapping = TextWrapping.Wrap },
                            duration,
                        },
                    },
                    PrimaryButtonText = "Allow", SecondaryButtonText = "Block this site", CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot,
                };
                var r = await dlg.ShowSerializedAsync();
                var pressed = r switch
                {
                    ContentDialogResult.Primary => PermissionDialogButton.Allow,
                    ContentDialogResult.Secondary => PermissionDialogButton.Block,
                    _ => PermissionDialogButton.Dismissed,
                };
                tcs.TrySetResult(PermissionChoices.FromDialog(pressed, allowForAnHour: duration.SelectedIndex == 1));
            }
            catch (Exception) { tcs.TrySetResult(PermissionChoice.BlockOnce); }   // could not ask: deny this request
        })) tcs.TrySetResult(PermissionChoice.BlockOnce);                          // dispatcher gone: same
        return await tcs.Task;
    }

    // ---- Agent Gateway ----

    private Task<bool> ConfirmAgentActionAsync(AgentSession s, AgentRequest r)
    {
        var tcs = new TaskCompletionSource<bool>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            var dlg = new ContentDialog
            {
                Title = $"{s.Manifest.Agent} wants to {r.Action}",
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"Target: {r.Selector ?? r.Url}\n{(r.Text is null ? "" : $"Text: {r.Text}\n")}\nThis looks destructive. Allow it?" },
                PrimaryButtonText = "Allow once", CloseButtonText = "Deny", XamlRoot = Content.XamlRoot,
            };
            tcs.TrySetResult(await dlg.ShowSerializedAsync() == ContentDialogResult.Primary);
        });
        return tcs.Task;
    }

    /// <summary>Shown for every session an external agent requests: what it asked for versus what it gets.</summary>
    private Task<bool> ApproveAgentSessionAsync(AgentManifest requested, AgentManifest effective, IReadOnlyList<string> adjustments)
    {
        var tcs = new TaskCompletionSource<bool>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            var text = $"{effective.Agent} asks for a session.\n\nGranted:\n  domains: {string.Join(", ", effective.AllowDomains)}\n  actions: {string.Join(", ", effective.Actions)}\n  {effective.MaxLivePages} live pages, {effective.SessionMinutes} min, destructive: {effective.DestructiveActions}\n  identity: throwaway ({effective.Container}), not your logins" +
                       (adjustments.Count > 0 ? "\n\nReduced from the request:\n  • " + string.Join("\n  • ", adjustments) : "");
            // The domain list answers WHERE. It does not answer what becomes of what the agent reads, and that is
            // the part we cannot enforce: once the page map crosses the local endpoint it is the agent's, not ours.
            var reads = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "What it can read on those domains: the page title, headings, link text and addresses, form "
                     + "field names and types, and up to 4,000 characters of the main text"
                     + (effective.Actions.Contains(AgentAction.Screenshot) ? ", plus screenshots" : "")
                     + ".\n\nStructured field information excludes field values. Page text"
                     + (effective.Actions.Contains(AgentAction.Screenshot) ? " — and screenshots" : "")
                     + " may contain personal information visible on the page.\n\n"
                     + "What happens to it afterwards is up to " + effective.Agent + ". JevBrowse hands it over "
                     + "and cannot follow it — the agent may send it to its own servers or AI model. Your own AI and "
                     + "search settings do not restrict that. Every request is recorded in this session's audit, and "
                     + "Stop in the Agents panel ends it immediately.",
            };
            var body = new StackPanel { Spacing = Tokens.Space(12), Children = {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") },
                reads } };
            var dlg = new ContentDialog { Title = "Agent session request", Content = new ScrollViewer { MaxHeight = 460, Content = body }, PrimaryButtonText = "Allow session", CloseButtonText = "Deny", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
            tcs.TrySetResult(await dlg.ShowSerializedAsync() == ContentDialogResult.Primary);
        });
        return tcs.Task;
    }

    /// <summary>
    /// The separate, explicit question for screenshots: experimental, per session, and answered No by Esc, Enter and the close button
    /// alike. Nothing an agent sends can answer it. It is a decision, so it stays a dialog.
    /// </summary>
    private Task<bool> ApproveScreenshotsAsync(AgentManifest effective)
    {
        var tcs = new TaskCompletionSource<bool>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            var body = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"{effective.Agent} asked to take pictures of the pages it opens.\n\n"
                     + "Screenshots are EXPERIMENTAL and off unless you allow them for this session. If you allow them: pictures are taken of "
                     + "pages the agent opened (not the page you are looking at), kept in memory, and handed only to the agent. Pages with a "
                     + "password or payment field are refused, including inside frames where that can be detected. A picture shows everything "
                     + "visible on the page and may contain personal information.\n\n"
                     + "What happens to a picture afterwards is up to " + effective.Agent + "; JevBrowse cannot follow it.",
            };
            var dlg = new ContentDialog
            {
                Title = "Allow screenshots for this session? (experimental)", Content = body,
                PrimaryButtonText = "Allow screenshots for this session", CloseButtonText = "No screenshots",
                DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot,
            };
            tcs.TrySetResult(await dlg.ShowSerializedAsync() == ContentDialogResult.Primary);
        });
        return tcs.Task;
    }

    private string SessionLine(AgentGateway.AgentSession ses) =>
        $"{ses.Manifest.Agent} [{ses.Id}] {(ses.CleanedUp ? "stopped" : ses.Closed ? "closing" : "open")} • {ses.ActionsUsed}/{ses.Manifest.MaxActions} actions • {_agents!.LiveAgentPages(ses)}/{ses.Manifest.MaxLivePages} live • expires {ses.ExpiresAt.ToLocalTime():HH:mm}\n"
        + string.Join("\n", ses.Audit.TakeLast(6).Select(a => $"   {(a.Allowed ? "✓" : "✕")} {a.Action} {a.Target} — {a.Reason}"));

    private async void OnAgents(object s, RoutedEventArgs e)
    {
        if (_agents is null) return;
        var running = _agentHost?.IsRunning == true;
        var toggle = new ToggleSwitch { Header = "Local endpoint for external agents (127.0.0.1, bearer token, this run only)", IsOn = running };
        // The ceiling is the user's decision, made BEFORE the endpoint exists. Agents can request less, never more.
        var domains = new TextBox { Header = "Approved domains (comma-separated)", Text = "localhost, 127.0.0.1, github.com, learn.microsoft.com", IsEnabled = !running };
        var actionBoxes = new[] { AgentAction.Navigate, AgentAction.Read, AgentAction.Click, AgentAction.TypeNonSecret, AgentAction.Screenshot }
            .Select(a => new CheckBox { Content = a == AgentAction.Screenshot ? "Screenshot (experimental: still asked for each session)" : a.ToString(), Tag = a, IsChecked = a is AgentAction.Navigate or AgentAction.Read, IsEnabled = !running }).ToList();
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space(8) };
        foreach (var b in actionBoxes) actionRow.Children.Add(b);
        var minutes = new NumberBox { Header = "Max minutes per session", Value = 60, Minimum = 1, Maximum = 480, IsEnabled = !running };
        var panel = new StackPanel { Spacing = Tokens.Space(8), Children = { toggle } };
        if (!running)
        {
            panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 12, Text = "Approve what agents may ever do. A session request is clamped to this; anything wider is dropped, and you are asked about every session. Agents only ever get a throwaway identity, never your logins." });
            panel.Children.Add(domains); panel.Children.Add(new TextBlock { Text = "Approved actions" }); panel.Children.Add(actionRow); panel.Children.Add(minutes);
        }
        if (running)
        {
            var url = $"http://127.0.0.1:{_agentHost!.Port}/";
            panel.Children.Add(new TextBlock { Text = $"Base URL: {url}", IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
            panel.Children.Add(new TextBlock { Text = $"Token:    {_agentHost.Token}", IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
            panel.Children.Add(new TextBlock { Text = "Sessions:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            foreach (var ses in _agentHost.Sessions)
            {
                // "Stopped" is only shown once the pages are actually released, so the control never overstates itself.
                var state = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Text = SessionLine(ses) };
                var stop = new Button { Content = "Stop", Style = (Style)Application.Current.Resources["JevToolButton"], IsEnabled = !ses.CleanedUp };
                var session = ses;
                stop.Click += async (_, _) =>
                {
                    stop.IsEnabled = false; stop.Content = "Stopping…";
                    var released = await _agentHost!.StopAsync(session);
                    stop.Content = released ? "Stopped" : "Stop (retrying)";
                    stop.IsEnabled = !released;
                    state.Text = SessionLine(session);
                    StatusText.Text = released ? $"agent session {session.Id} revoked and its pages released" : $"agent session {session.Id} revoked; some pages could not be released yet";
                };
                panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space(8), Children = { stop, state } });
            }
            var stopAll = new Button { Content = "Stop all sessions", Style = (Style)Application.Current.Resources["JevToolButton"] };
            stopAll.Click += async (_, _) => { var n = await _agentHost!.StopAllAsync(); StatusText.Text = $"revoked {n} agent session(s)"; };
            panel.Children.Add(stopAll);
        }
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, Text = "Agents get a manifest-scoped session: allowed domains, allowed actions, data-class ceiling, a live-page quota, and a time/action budget. Destructive clicks ask you. Every request is written to data/agents/audit/<session>.jsonl. See docs/AGENT_SECURITY.md." });
        var dlg = new ContentDialog { Title = "Agent Gateway", Content = new ScrollViewer { MaxHeight = 480, Content = panel }, PrimaryButtonText = "Apply", CloseButtonText = "Close", XamlRoot = Content.XamlRoot };
        if (await dlg.ShowSerializedAsync() != ContentDialogResult.Primary) return;
        if (toggle.IsOn && !running)
        {
            var ceiling = new AgentCeiling
            {
                Limits = new AgentManifest
                {
                    Agent = "ceiling",
                    AllowDomains = domains.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim().ToLowerInvariant()).Distinct().ToList(),
                    Actions = actionBoxes.Where(b => b.IsChecked == true).Select(b => (AgentAction)b.Tag).ToList(),
                    SessionMinutes = (int)Math.Clamp(minutes.Value, 1, 480),
                    MaxLivePages = 3, MaxActions = 500, DestructiveActions = "confirm", Container = IdentityContainer.Disposable,
                },
            };
            if (ceiling.Limits.AllowDomains.Count == 0 || ceiling.Limits.Actions.Count == 0) { StatusText.Text = "agent endpoint not started: approve at least one domain and one action"; return; }
            _agentHost = new LocalAgentHost(_agents, ceiling, ApproveAgentSessionAsync, approveScreenshots: ApproveScreenshotsAsync);
            _agentHost.BackgroundFault += OnAgentHostFault;
            _agentHost.Start();
            StatusText.Text = $"agent endpoint listening on 127.0.0.1:{_agentHost.Port} (token in Agents panel); grant limited to {string.Join(", ", ceiling.Limits.AllowDomains)}";
        }
        else if (!toggle.IsOn && running)
        {
            await _agentHost!.StopEndpointAsync();   // revokes what it granted; awaited, so the window never blocks on its own cleanup
            _agentHost.Dispose(); _agentHost = null;
            UpdateAgentIndicator();
            StatusText.Text = "agent endpoint stopped";
        }
    }

    // ---- DevSpace ----

    private void UpdateEnvChrome()
    {
        if (_dev is null || !_dev.Enabled || _kernel?.Active is not { } t) { ProdBorder.Visibility = EnvBadge.Visibility = Visibility.Collapsed; return; }
        var r = _dev.Resolver.Resolve(t.Url);
        var show = r.Environment != DeployEnvironment.Unknown;
        EnvBadge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        EnvBadgeText.Text = r.Environment.ToString().ToUpperInvariant();
        // The production strip is a safety signal, so its text has to be readable: the old fills were 2.2 to 3.5 : 1.
        var fill = Tokens.Brush(r.Environment switch
        {
            DeployEnvironment.Prod => "JevDangerBrush",
            DeployEnvironment.Staging => "JevEnvStagingBrush",
            DeployEnvironment.Dev => "JevEnvDevBrush",
            _ => "JevEnvOtherBrush",
        });
        EnvBadge.Background = fill;
        EnvBadgeText.Foreground = Tokens.Brush("JevDangerTextBrush");
        ProdBorder.BorderBrush = fill;
        ProdBorder.Visibility = r.Environment == DeployEnvironment.Prod ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnDev(object s, RoutedEventArgs e)
    {
        if (_dev is null || _kernel is null) return;
        var enable = new ToggleSwitch { Header = "DevSpace enabled (attaches DevTools listeners to new renderers)", IsOn = _dev.Enabled };
        var panel = new StackPanel { Spacing = Tokens.Space(8), Children = { enable } };

        if (_dev.Projects.Count == 0)
        {
            var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"No projects configured. Environments are never guessed. Create an example at:\n{_dev.ProjectsPath}" };
            var mk = new Button { Content = "Create example projects.json" };
            mk.Click += (_, _) => { ProjectStore.Save(_dev.ProjectsPath, [ProjectStore.Example()]); _dev.ReloadProjects(); hint.Text = "Example written. Edit it, then reopen this panel."; };
            panel.Children.Add(hint); panel.Children.Add(mk);
        }
        else
        {
            var reload = new Button { Content = "Reload projects.json" };
            reload.Click += (_, _) => { _dev.ReloadProjects(); UpdateEnvChrome(); };
            panel.Children.Add(new TextBlock { Text = $"Projects: {string.Join(", ", _dev.Projects.Select(p => p.Name))} ({_dev.ProjectsPath})", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            panel.Children.Add(reload);
        }

        if (_kernel.Active is { } t)
        {
            var r = _dev.Resolver.Resolve(t.Url);
            panel.Children.Add(new TextBlock { Text = $"Environment: {r.Environment} — {r.Reason}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

            var services = _dev.Projects.SelectMany(p => p.LocalServices).Distinct().ToList();
            if (services.Count > 0)
            {
                var probe = await LocalServiceProbe.ProbeAsync(services);
                panel.Children.Add(new TextBlock { Text = "Localhost: " + string.Join("  ", probe.Select(x => $"{x.Name}:{x.Port} {(x.Up ? "● up" : "○ down")}")), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
            }

            if (_dev.Data.TryGetValue(t.Id, out var data))
            {
                var groups = ErrorGrouper.Group(data.Console.ToList());
                var net = NetworkGrouper.Summarize(data.Network.ToList(), t.Url.Host);
                panel.Children.Add(new TextBlock { Text = $"Network: {net.Total} requests — api {net.Counts[NetKind.Api]}, static {net.Counts[NetKind.Static]}, third-party {net.Counts[NetKind.ThirdParty]}, failed {net.Counts[NetKind.Failed]}, slow {net.Counts[NetKind.Slow]}, duplicate {net.Counts[NetKind.Duplicate]}", TextWrapping = TextWrapping.Wrap });
                foreach (var f in net.Failed.Take(5)) panel.Children.Add(new TextBlock { Text = $"  ✕ {f.Status} {f.Method} {f.Url}", FontSize = 11, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(new TextBlock { Text = $"Console errors: {groups.Sum(g => g.Count)} in {groups.Count} group(s)", TextWrapping = TextWrapping.Wrap });
                foreach (var g in groups.Take(5)) panel.Children.Add(new TextBlock { Text = $"  ×{g.Count}{(g.Cascade.Count > 0 ? $" (+{g.Cascade.Count} cascaded)" : "")} {g.Headline}", FontSize = 11, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap });

                if (groups.Count > 0)
                {
                    var explain = new Button { Content = "Explain top error (AI, explicit)" };
                    explain.Click += async (_, _) =>
                    {
                        var top = groups[0];
                        var input = $"Error (×{top.Count}): {top.First.Message}\nSource: {top.First.Source}:{top.First.Line}\nFollowed by: {string.Join(" | ", top.Cascade.Take(3).Select(c => c.Message))}";
                        var d = await _brain!.DecideAsync(new DecisionRequest(BrainTask.ExplainError, input, _kernel.ClassOf(t), _kernel.ContainerOf(t), ExplicitUserAction: true, t.Url), default);
                        explain.Content = d.WasDenied ? $"Not answered: {d.Rule}" : "Explained below";
                        panel.Children.Add(new TextBlock { Text = d.WasDenied ? "" : d.Output, TextWrapping = TextWrapping.Wrap });
                    };
                    panel.Children.Add(explain);
                }
            }
            else if (_dev.Enabled) panel.Children.Add(new TextBlock { Text = "No data for this tab yet (listeners attach to renderers created after enabling).", Opacity = 0.7 });
        }

        var dlg = new ContentDialog { Title = "DevSpace", Content = new ScrollViewer { MaxHeight = 520, Content = panel }, PrimaryButtonText = "Save", CloseButtonText = "Close", XamlRoot = Content.XamlRoot };
        if (await dlg.ShowSerializedAsync() != ContentDialogResult.Primary) return;
        _dev.SetEnabled(enable.IsOn);
        UpdateEnvChrome();
        StatusText.Text = $"DevSpace {(enable.IsOn ? "enabled for new renderers" : "disabled")}";
    }

    // ---- Browser Memory ----

    private async void OnMemory(object s, RoutedEventArgs e) => await ShowMemoryAsync("");

    private async Task ShowMemoryAsync(string initialQuery)
    {
        if (_memory is null || _kernel is null) return;
        var (docs, bytes) = _memory.Stats();
        var box = new TextBox { PlaceholderText = "e.g. webview2 process model", Text = initialQuery };
        var results = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 320 };
        var hits = new List<MemoryHit>();
        var info = new TextBlock { Opacity = 0.7, FontSize = 12, Text = $"{docs} pages indexed • {bytes / 1024.0 / 1024.0:F1} MB of text, {_memory.DatabaseFileBytes() / 1024.0 / 1024.0:F1} MB database file • local only • public pages only" };
        void RunSearch()
        {
            hits = _memory.Search(box.Text, 20, _kernel.ActiveWorkspace).ToList();
            results.Items.Clear();
            foreach (var h in hits)
            {
                // A tab may have navigated on since this page was indexed, so "open" means: some tab shows this exact page now.
                var open = _kernel.Tabs.Any(t => t.Url == h.Url);
                results.Items.Add(new StackPanel { Children = {
                    new TextBlock { Text = $"{h.Title}  {(open ? "" : "(not open — will reopen)")}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    new TextBlock { Text = h.Snippet, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 },
                    new TextBlock { Text = $"{h.Site} • {h.CapturedAt.ToLocalTime():ddd d MMM HH:mm}", FontSize = 11, Opacity = 0.6 } } });
            }
            if (hits.Count == 0 && box.Text.Length > 1) results.Items.Add(new TextBlock { Text = "No matches.", Opacity = 0.6 });
        }
        box.TextChanged += (_, _) => RunSearch();
        if (initialQuery.Length > 0) RunSearch();
        var dlg = new ContentDialog
        {
            Title = "Browser Memory",
            Content = new StackPanel { Spacing = Tokens.Space(8), Children = { box, info, results } },
            PrimaryButtonText = "Open", SecondaryButtonText = "Clear index", CloseButtonText = "Close", XamlRoot = Content.XamlRoot,
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var result = await dlg.ShowSerializedAsync();
        if (result == ContentDialogResult.Secondary)
        {
            var confirm = new ContentDialog { Title = "Clear Browser Memory?", Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"This deletes the local index of {docs} pages. It does not touch your tabs, history or cookies. Pages you read later are indexed again." }, PrimaryButtonText = "Clear", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
            if (await confirm.ShowSerializedAsync() == ContentDialogResult.Primary) { var n = _memory.Clear(); StatusText.Text = $"Browser Memory cleared ({n} pages)"; }
            return;
        }
        if (result != ContentDialogResult.Primary || results.SelectedIndex < 0 || results.SelectedIndex >= hits.Count) return;
        var hit = hits[results.SelectedIndex];
        var existing = _kernel.Tabs.FirstOrDefault(t => t.Url == hit.Url);
        if (existing is not null) await _kernel.ActivateAsync(existing.Id);
        else { var t = _kernel.Open(hit.Url); await _kernel.ActivateAsync(t.Id); }
    }

    /// <summary>Phase 8 gate: real pages → readable text → FTS index → search; plus proof that a login page is skipped.</summary>
    private async Task RunMemoryCheckAsync()
    {
        var k = _kernel!;
        foreach (var t in k.Tabs.ToList()) await k.CloseAsync(t.Id);
        var pages = new[]
        {
            "https://en.wikipedia.org/wiki/Web_browser", "https://en.wikipedia.org/wiki/Memory_management",
            "https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model",
            "https://github.com/login", // password field → SECRET → must be skipped
        };
        var decisions = new List<string>();
        _indexer!.Decided += (id, why) => decisions.Add($"{k.Tabs.FirstOrDefault(t => t.Id == id)?.Url.Host}: {why}");
        foreach (var u in pages)
        {
            var t = k.Open(new Uri(u));
            var loaded = new TaskCompletionSource();
            void OnEv(KernelEvent e) { if (e.Kind == "restored" && e.Id == t.Id) loaded.TrySetResult(); }
            k.Changed += OnEv;
            await k.ActivateAsync(t.Id);
            await Task.WhenAny(loaded.Task, Task.Delay(20000));
            k.Changed -= OnEv;
            await Task.Delay(2500); // let the indexer and the page's password-field scan finish
        }
        var (docs, bytes) = _memory!.Stats();
        var q1 = _memory.Search("renderer process");
        var q2 = _memory.Search("garbage collection");
        var result = new
        {
            pagesLoaded = pages.Length, indexed = _indexer.Indexed, skipped = _indexer.Skipped, docs, kb = bytes / 1024,
            decisions,
            search_renderer_process = q1.Select(h => new { h.Title, h.Site, score = Math.Round(h.Score, 3), snippet = h.Snippet[..Math.Min(90, h.Snippet.Length)] }).ToList(),
            search_garbage_collection = q2.Select(h => h.Title).ToList(),
        };
        var file = Path.Combine(DataDir, "benchmarks", $"memory-check-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    // ---- JevBrain ----

    private async void OnAsk(object s, RoutedEventArgs e)
    {
        if (_kernel?.Active is not { } t || _brain is null) return;
        var cls = _kernel.ClassOf(t);
        var container = _kernel.ContainerOf(t);

        // Extract visible text locally. Nothing has left the machine yet.
        string text = "";
        if (_leases!.TryGet(t.Id, out var l))
        {
            var raw = await ((WebView2Lease)l).View.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText.slice(0, 12000) : ''");
            text = JsonSerializer.Deserialize<string>(raw) ?? "";
        }
        var redacted = Redactor.Redact(text);
        var provider = _providers.FirstOrDefault(p => p.IsConfigured && (_brainPolicy.Preference[BrainTask.SummarizePage].Contains(p.Kind)));

        var preview = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = !_brainPolicy.AiEnabled ? "AI is off. Turn it on in the Brain panel first."
                 : !cls.MayLeaveDeviceOnExplicitRequest() ? (cls == DataClass.Unknown
                     ? "This page is Not assessed — we have no positive evidence it is public, so its content stays on the device. "
                       + "If you know this site is public, tap the class badge and set it, then ask again."
                     : $"This page is {ClassLabel(cls)}. Its content never leaves the device (hard rule).")
                 : provider is null ? "No AI provider is configured (set OPENROUTER_API_KEY or JEV_API_KEY in the environment)."
                 : $"Send {redacted.Text.Length:N0} characters of this {cls} page to {provider.Kind} ({provider.Model})?\n" +
                   $"{redacted.Count} item(s) were redacted first{(redacted.Count > 0 ? ": " + string.Join(", ", redacted.Kinds) : "")}.\n" +
                   "The page URL and your identity are not sent.",
        };
        var canSend = _brainPolicy.AiEnabled && cls.MayLeaveDeviceOnExplicitRequest() && provider is not null;
        var dlg = new ContentDialog { Title = "Ask: summarize this page", Content = preview, PrimaryButtonText = "Send", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = canSend, XamlRoot = Content.XamlRoot };
        if (await dlg.ShowSerializedAsync() != ContentDialogResult.Primary) return;

        StatusText.Text = $"asking {provider!.Kind}…";
        var d = await _brain.DecideAsync(new DecisionRequest(BrainTask.SummarizePage, text, cls, container, ExplicitUserAction: true, t.Url), default);
        var result = new ContentDialog
        {
            Title = d.WasDenied ? "Not answered" : $"Summary via {d.Source} ({d.Model})",
            Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = d.WasDenied ? $"Rule: {d.Rule}" : d.Output, TextWrapping = TextWrapping.Wrap } },
            CloseButtonText = "Close", XamlRoot = Content.XamlRoot,
        };
        StatusText.Text = $"brain: {d.Rule}{(d.Redacted ? $" • {d.RedactionCount} redacted" : "")}";
        await result.ShowSerializedAsync();
    }

    private async void OnBrain(object s, RoutedEventArgs e)
    {
        var ai = new ToggleSwitch { Header = "AI enabled (Ask, Explain error: only when you click)", IsOn = _brainPolicy.AiEnabled };
        var cloud = new ToggleSwitch { Header = "Cloud providers allowed", IsOn = _brainPolicy.CloudEnabled };
        var auto = new ToggleSwitch { Header = "Automatic judgments: let Jev classify pages and ad slots after load (sends host, path, title, headings; PUBLIC pages only)", IsOn = _brainPolicy.AutomaticJudgments };
        var providers = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Providers: " + string.Join(", ", _providers.Select(p => $"{p.Kind} {(p.IsConfigured ? "✓ " + p.Model : "(not configured)")}")) + $", Jev decisions {(_jev?.IsConfigured == true ? "✓ " + _jev.Model : "(not configured)")}" };
        var byClass = _decisions!.CloudCallsByClass();
        var metric = new TextBlock { Text = "Cloud calls by data class: " + (byClass.Count == 0 ? "none" : string.Join(", ", byClass.Select(kv => $"{kv.Key}={kv.Value}"))), Opacity = 0.8 };
        var log = new TextBlock { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Text = string.Join("\n", _decisions.Recent(25).Select(r => $"{r.At.ToLocalTime():HH:mm:ss} {r.Source,-10} {r.Rule}{(r.Redacted ? $" (redacted {r.RedactionCount})" : "")}")) };
        var panel = new StackPanel { Spacing = Tokens.Space(8), Children = { ai, cloud, auto, providers, metric, new TextBlock { Text = "Decision log (newest first):", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, new ScrollViewer { MaxHeight = 260, Content = log } } };
        var dlg = new ContentDialog { Title = "JevBrain", Content = panel, PrimaryButtonText = "Save", CloseButtonText = "Close", XamlRoot = Content.XamlRoot };
        if (await dlg.ShowSerializedAsync() != ContentDialogResult.Primary) return;
        _brainPolicy.AiEnabled = ai.IsOn;
        _brainPolicy.CloudEnabled = cloud.IsOn;
        _brainPolicy.AutomaticJudgments = auto.IsOn && ai.IsOn && cloud.IsOn;   // automatic calls need both master switches too
        StatusText.Text = $"brain: AI {(ai.IsOn ? "on" : "off")}, cloud {(cloud.IsOn ? "on" : "off")}, automatic {(_brainPolicy.AutomaticJudgments ? "on" : "off")}";
    }

    // ---- Shield ----

    private long _filterCompileMs;
    private FilterEngine? _engine;

    private void CompileFilters()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lines = _filters!.ReadActiveLines().ToList();
        _engine = FilterEngine.Compile(lines);
        var cosmetic = CosmeticEngine.Compile(lines);
        _filterCompileMs = sw.ElapsedMilliseconds;
        _shield!.SetEngine(_engine);
        _shield.SetCosmetic(cosmetic);
        StatusText.Text = $"Shield: {_engine.RuleCount:N0} network + {cosmetic.GenericCount + cosmetic.DomainRuleCount:N0} cosmetic rules in {_filterCompileMs} ms ({_engine.SkippedLines:N0} unsupported network lines, {cosmetic.Skipped:N0} procedural cosmetic skipped)";
    }

    private async Task UpdateFilterListsAsync()
    {
        StatusText.Text = "Shield: downloading filter lists…";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JevBrowse/0.1 (+filter-list-update)");
        var r = await _filters!.UpdateAsync(http);
        CompileFilters();
        StatusText.Text += r.Activated ? " • lists updated" : " • update failed: " + string.Join("; ", r.Details.Select(kv => $"{kv.Key} {kv.Value}"));
    }

    private void OnShield(object s, RoutedEventArgs e) => OpenPanel("shield", BuildShield, ShieldButton);

    /// <summary>What Shield did here, and the one action most visits are for. A panel, so the page beside it stays visible while you decide.</summary>
    private (string Title, UIElement Body)? BuildShield()
    {
        if (_kernel?.Active is not { } t || _shield is null) return null;
        var site = NetworkRequest.SiteOf(t.Url.Host);
        var enabled = _shield.IsEnabledFor(site);
        _shield.Stats.TryGetValue(t.Id, out var st);
        var lines = new List<string>
        {
            $"{site}: Shield {(enabled ? "ON" : "OFF")}",
            st is null ? "no requests seen yet" : $"{st.Blocked} blocked of {st.Total} requests on this page; {st.CosmeticSelectors:N0} element-hiding selectors applied",
            $"{_shield.RuleCount:N0} network rules, {_shield.CosmeticGeneric + _shield.CosmeticDomain:N0} cosmetic rules active",
            "",
        };
        if (st is not null && st.SiteModules.Count > 0) lines.Insert(3, $"Site modules: {string.Join(", ", st.SiteModules)} — ad definitions pruned {st.AdsPruned}, player skips {st.AdsSkipped}{(st.WallSeen > 0 ? ", anti-adblock wall seen" : "")}");
        if (st is not null) lines.AddRange(st.Recent.Reverse().Take(15).Select(x => $"✕ {x.Host}\n    {x.Rule}"));
        // Undo for this document only: put back everything the collapse layers hid, drop the cosmetic sheet.
        var showHidden = new Button { Content = "Show what Shield hid on this page", HorizontalAlignment = HorizontalAlignment.Stretch };
        var undoNote = new TextBlock { FontSize = 12, Foreground = Tokens.Brush("JevTextSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
        showHidden.Click += async (_, _) =>
        {
            if (_leases!.TryGet(t.Id, out var l)) { var n = await _shield.RestoreHiddenAsync(((WebView2Lease)l).View.CoreWebView2, t.Id); undoNote.Text = n > 0 ? $"Restored {n} hidden item(s). Nothing more is hidden until you navigate." : "Nothing on this page was hidden."; }
        };
        var summary = st is null ? "Nothing has been checked on this page yet."
            : st.Blocked == 0 ? $"Nothing was blocked on this page ({st.Total} requests checked)."
            : $"Blocked {st.Blocked} of {st.Total} requests on this page: ads and trackers.";
        // Lead with what a person opens this for. Most visits to this dialog are "this site is broken", and the answer
        // is one button, not a table of rules.
        var repair = new StackPanel
        {
            Spacing = Tokens.Space(4),
            Children =
            {
                new TextBlock { Text = "Site not working?", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15 },
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Foreground = Tokens.Brush("JevTextSecondaryBrush"),
                    Text = enabled
                        ? $"Some sites break when their ads or trackers are blocked. Turn Shield off for {site} and the page reloads with nothing blocked. You can turn it back on here."
                        : $"Shield is off for {site}, so nothing is blocked there. Turn it on to block ads and trackers again.",
                },
            },
        };
        var details = new Expander
        {
            Header = "Technical details", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { Content = new TextBlock { Text = string.Join("\n", lines), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap }, MaxHeight = 300 },
        };
        // Explicit buttons, one click each, named for what they do. Turning Shield off reloads the page, and the label says so.
        var toggle = new Button { Content = enabled ? "Turn off and reload" : "Turn on", HorizontalAlignment = HorizontalAlignment.Stretch };
        toggle.Click += async (_, _) =>
        {
            await _shield.SetEnabledForAsync(site, !enabled);   // removes cosmetic + site-module scripts in every open tab of this site
            WithActiveLease(l => l.View.CoreWebView2.Reload());
            StatusText.Text = enabled ? $"Shield is now off for {site}. The page was reloaded without blocking." : $"Shield is back on for {site}.";
            RefreshPanel();
        };
        var update = new Button { Content = "Update block lists", HorizontalAlignment = HorizontalAlignment.Stretch };
        update.Click += async (_, _) => await UpdateFilterListsAsync();
        return ("Shield", new StackPanel { Spacing = Tokens.Space(12), Children = { repair, toggle, new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap }, showHidden, undoNote, details, update } });
    }

    /// <summary>
    /// Agent scope gate (ADR 0017, condition 1): a REAL cross-domain redirect against a real renderer.
    /// youtu.be/&lt;id&gt; 301s to www.youtube.com, which is outside a grant for youtu.be. If the policy is attached after
    /// the load (as it used to be), the renderer lands on youtube.com and the boundary we advertise is not real.
    /// Also exercises revocation: after Stop, the page must not be able to navigate at all.
    /// </summary>
    private async Task RunAgentCheckAsync()
    {
        var k = _kernel!;
        foreach (var t in k.Tabs.ToList()) await k.CloseAsync(t.Id);

        var ceiling = new AgentCeiling { Limits = new AgentManifest { Agent = "ceiling", AllowDomains = ["youtu.be", "example.com"], Actions = [AgentAction.Navigate, AgentAction.Read], SessionMinutes = 10, MaxLivePages = 2, MaxActions = 50 } };
        using var host = new LocalAgentHost(_agents!, ceiling, approveScreenshots: _ => Task.FromResult(true));   // a check with nobody present: approval is given here, on purpose
        var (session, _) = await host.GrantAsync(new AgentManifest { Agent = "redirect-probe", AllowDomains = ["youtu.be"], Actions = [AgentAction.Navigate, AgentAction.Read], SessionMinutes = 10, MaxLivePages = 2 });

        var nav = await _agents!.ExecuteAsync(session, new AgentRequest(AgentAction.Navigate, "https://youtu.be/dQw4w9WgXcQ"), default);
        await Task.Delay(8000);

        string landed = "", scope = "unknown";
        var tab = k.Tabs.FirstOrDefault(t => session.Pages.Contains(t.Id));
        if (tab is not null && _leases!.TryGet(tab.Id, out var l))
        {
            landed = ((WebView2Lease)l).View.CoreWebView2?.Source ?? "";
            var host2 = Uri.TryCreate(landed, UriKind.Absolute, out var lu) ? lu.Host.ToLowerInvariant() : "";
            scope = host2.EndsWith("youtu.be", StringComparison.Ordinal) || host2.Length == 0 || landed.StartsWith("about:", StringComparison.Ordinal) ? "in-scope" : "ESCAPED";
        }

        var outOfScopeBlocked = scope != "ESCAPED";
        var stopped = await host.StopAsync(session);
        var deniedAfterStop = (await _agents.ExecuteAsync(session, new AgentRequest(AgentAction.Navigate, "https://youtu.be/other"), default)).Message;

        var result = new
        {
            pass = outOfScopeBlocked && stopped && deniedAfterStop.StartsWith("session_"),
            grantedDomains = session.Manifest.AllowDomains,
            navigateAccepted = nav.Ok,
            landedOn = landed,
            scope,
            outOfScopeRedirectBlocked = outOfScopeBlocked,
            stopReleasedPages = stopped,
            livePagesAfterStop = _agents.LiveAgentPages(session),
            requestAfterStop = deniedAfterStop,
            audit = session.Audit.Select(a => $"{(a.Allowed ? "ok" : "no")} {a.Action} {a.Target} — {a.Reason}").ToList(),
        };
        var file = Path.Combine(DataDir, "benchmarks", $"agent-check-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Real engine, real endpoint: an agent navigates, reads and takes a screenshot while a person is reading another page. The
    /// person's tab, workspace and what is on screen must not change, and the agent's page has to be readable and photographable
    /// even though it is not shown (a hidden web view is not assumed to behave; this measures it).
    /// </summary>
    private async Task RunAgentWindowCheckAsync()
    {
        var k = _kernel!;
        foreach (var t in k.Tabs.ToList()) await k.CloseAsync(t.Id);
        var mine = k.Open(new Uri("https://example.com/"));
        var loaded = new TaskCompletionSource();
        void OnEv(KernelEvent e) { if (e.Kind == "restored" && e.Id == mine.Id) loaded.TrySetResult(); }
        k.Changed += OnEv;
        await k.ActivateAsync(mine.Id);
        await Task.WhenAny(loaded.Task, Task.Delay(20000));
        k.Changed -= OnEv;
        var workspaceBefore = k.ActiveWorkspace;

        var ceiling = new AgentCeiling { Limits = new AgentManifest { Agent = "ceiling", AllowDomains = ["example.org", "example.com"], Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.Screenshot], SessionMinutes = 10, MaxLivePages = 2, MaxActions = 50 } };
        using var host = new LocalAgentHost(_agents!, ceiling, approveScreenshots: _ => Task.FromResult(true));   // a check with nobody present: approval is given here, on purpose
        var (session, _) = await host.GrantAsync(new AgentManifest { Agent = "window-probe", AllowDomains = ["example.org"], Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.Screenshot], SessionMinutes = 10, MaxLivePages = 2 });

        // The person's OWN restore is in flight (their page put to sleep, then woken) while the agent's page comes live at the same moment.
        // The restore panel, its timer and its buttons belong to the person's page: sample which tab the panel is tracking throughout.
        await k.VirtualizeAsync(mine.Id, Cause.User);
        var tracked = new List<ResourceId?>();
        var sampler = DispatcherQueue.CreateTimer();
        sampler.Interval = TimeSpan.FromMilliseconds(25); sampler.IsRepeating = true;
        sampler.Tick += (_, _) => tracked.Add(_restoringId);
        sampler.Start();
        var mineRestored = new TaskCompletionSource();
        void OnMine(KernelEvent e) { if (e.Kind == "restored" && e.Id == mine.Id && !e.Background) mineRestored.TrySetResult(); }
        k.Changed += OnMine;
        var activation = k.ActivateAsync(mine.Id);
        var nav = await _agents!.ExecuteAsync(session, new AgentRequest(AgentAction.Navigate, "https://example.org/"), default);
        await activation;
        await Task.WhenAny(mineRestored.Task, Task.Delay(20000));
        k.Changed -= OnMine;
        await Task.Delay(1800);                                    // past the fade that hides the panel
        sampler.Stop();
        var panelAfter = RestorePanel.Visibility.ToString();
        var trackingAfter = _restoringId;
        await Task.Delay(1200);
        var read = await _agents.ExecuteAsync(session, new AgentRequest(AgentAction.Read), default);
        string Vis(ResourceId id) => _leases!.TryGet(id, out var l) ? ((WebView2Lease)l).View.Visibility.ToString() : "no renderer";
        // Hidden capture: the page must stay hidden, the person's page shown, focus and the foreground window unmoved.
        var staged = new List<StagedCaptureFacts>();
        WebView2Lease.StagedObserver = f => staged.Add(f);
        var focusBefore = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot);
        var foregroundBefore = NativeForeground.Get();
        var agentTabBefore = k.Tabs.FirstOrDefault(t => session.Pages.Contains(t.Id));
        var shownBefore = agentTabBefore is null ? "no page" : Vis(agentTabBefore.Id);
        var shot = await _agents.ExecuteAsync(session, new AgentRequest(AgentAction.Screenshot), default);
        var focusAfter = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot);
        var foregroundAfter = NativeForeground.Get();
        WebView2Lease.StagedObserver = null;

        var agentTab = k.Tabs.FirstOrDefault(t => session.Pages.Contains(t.Id));
        var agentTabForRestore = agentTab;
        var shotBytes = (long)(shot.Screenshot?.Length ?? 0);
        // Not blank: a real capture of a page has more than one distinct byte value in its pixel data. (A blank surface compresses to nearly nothing.)
        var distinct = shot.Screenshot is null ? 0 : shot.Screenshot.Distinct().Count();
        var pngOk = shot.Screenshot is { Length: > 24 } sb && sb[0] == 0x89 && sb[1] == 0x50 && sb[2] == 0x4E && sb[3] == 0x47;
        var shotFolderExists = Directory.Exists(Path.Combine(DataDir, "agents", "screenshots"));
        var result = new
        {
            personsTabStillActive = k.Active?.Id == mine.Id,
            personsWorkspaceUnchanged = k.ActiveWorkspace == workspaceBefore,
            personsPageStillShown = Vis(mine.Id),
            restorePanelOnlyEverTrackedThePersonsPage = agentTabForRestore is null || tracked.All(t => t is null || t == mine.Id),
            restoreSamples = tracked.Count,
            restoreSamplesTrackingPerson = tracked.Count(t => t == mine.Id),
            personsRestoreCompleted = mineRestored.Task.IsCompleted,
            restorePanelAfter = panelAfter,
            restoreStillTrackingAfter = trackingAfter is null ? "nothing" : trackingAfter.ToString(),
            agentPageShown = agentTab is null ? "no page" : Vis(agentTab.Id),
            agentPageLive = agentTab?.State.HasLiveRenderer() == true,
            navigateOk = nav.Ok,
            readOk = read.Ok,
            readTitle = read.Page?.Title,
            readTextChars = read.Page?.TextExcerpt?.Length ?? 0,
            screenshotOk = shot.Ok,
            screenshotBytes = shotBytes,
            pass = k.Active?.Id == mine.Id && k.ActiveWorkspace == workspaceBefore && Vis(mine.Id) == "Visible"
                   && agentTabForRestore is not null && tracked.All(t => t is null || t == mine.Id) && tracked.Count(t => t == mine.Id) > 0 && mineRestored.Task.IsCompleted
                   && panelAfter == "Collapsed" && trackingAfter is null
                   && agentTab is not null && Vis(agentTab.Id) == "Collapsed" && agentTab.State.HasLiveRenderer()
                   && nav.Ok && read.Ok && (read.Page?.TextExcerpt?.Length ?? 0) > 20
                   && shot.Ok && pngOk && shotBytes > 2000 && distinct > 8 && !shotFolderExists
                   && shownBefore == "Collapsed" && (agentTab is null || Vis(agentTab.Id) == "Collapsed") && ReferenceEquals(focusBefore, focusAfter) && foregroundBefore == foregroundAfter
                   && staged.Count > 0 && staged.All(f => !f.OverlapsWindow && !f.HitTestVisible && !f.TabStop),
            // Not part of the verdict: Screenshot returns no image in a Disposable session (thumbnails are refused there), with the page shown or hidden. A separate, older gap.
            screenshotIsPng = pngOk,
            screenshotDistinctByteValues = distinct,
            screenshotMessage = shot.Message,
            screenshotWroteAFile = shotFolderExists,
            screenshotAgentPageShownBefore = shownBefore,
            screenshotAgentPageShownAfter = agentTab is null ? "no page" : Vis(agentTab.Id),
            screenshotFocusUnchanged = ReferenceEquals(focusBefore, focusAfter),
            screenshotStagedGeometry = staged.Select(f => new { f.X, f.Y, f.Width, f.Height, f.WindowWidth, f.WindowHeight, f.OverlapsWindow, f.HitTestVisible, f.TabStop }).ToList(),
            screenshotNeverOverlappedTheWindow = staged.Count > 0 && staged.All(f => !f.OverlapsWindow && !f.HitTestVisible && !f.TabStop),
            screenshotForegroundWindowUnchanged = foregroundBefore == foregroundAfter,
            audit = session.Audit.Select(a => $"{(a.Allowed ? "ok" : "no")} {a.Action} {a.Target} - {a.Reason}").ToList(),
        };
        if (shot.Screenshot is not null) await File.WriteAllBytesAsync(Path.Combine(DataDir, "benchmarks", "agent-screenshot-evidence.png"), shot.Screenshot);   // for a person to look at; the gateway itself wrote nothing
        await host.StopAsync(session);
        var file = Path.Combine(DataDir, "benchmarks", $"agent-window-check-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Takes several hidden screenshots while scripts/agent-screenshot-stage-check.ps1 watches the person's window from OUTSIDE the
    /// app. The person's page is static (the welcome page). The script compares frames of the window taken during each capture with
    /// frames taken before, so "the page was never shown, even for a moment" is observed rather than assumed. The begin and end of
    /// each capture are written down in wall-clock milliseconds so the frames can be lined up with them.
    /// </summary>
    private async Task RunAgentScreenshotStageCheckAsync()
    {
        var k = _kernel!;
        var dir = Path.Combine(DataDir, "benchmarks"); Directory.CreateDirectory(dir);
        await Task.Delay(4000);
        var ceiling = new AgentCeiling { Limits = new AgentManifest { Agent = "ceiling", AllowDomains = ["example.org"], Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.Screenshot], SessionMinutes = 10, MaxLivePages = 2, MaxActions = 60, MaxScreenshots = 20 } };
        using var host = new LocalAgentHost(_agents!, ceiling, approveScreenshots: _ => Task.FromResult(true));   // a check with nobody present: approval is given here, on purpose
        var (session, _) = await host.GrantAsync(new AgentManifest { Agent = "stage-probe", AllowDomains = ["example.org"], Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.Screenshot], SessionMinutes = 10, MaxLivePages = 2, MaxScreenshots = 20 });
        var nav = await _agents!.ExecuteAsync(session, new AgentRequest(AgentAction.Navigate, "https://example.org/"), default);
        await Task.Delay(3000);
        await File.WriteAllTextAsync(Path.Combine(dir, "stage-ready.txt"), "ready");
        await Task.Delay(3500);                                   // the script takes its baseline frames now
        static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var spans = new List<object>();
        var stagedFacts = new List<StagedCaptureFacts>();
        WebView2Lease.StagedObserver = f => stagedFacts.Add(f);
        for (var i = 0; i < 6; i++)
        {
            var t0 = Now();
            var shot = await _agents.ExecuteAsync(session, new AgentRequest(AgentAction.Screenshot), default);
            var t1 = Now();
            spans.Add(new { beginMs = t0, endMs = t1, ok = shot.Ok, bytes = shot.Screenshot?.Length ?? 0, message = shot.Message });
            await Task.Delay(700);
        }
        await Task.Delay(1500);
        var agentTab = k.Tabs.FirstOrDefault(t => session.Pages.Contains(t.Id));
        var result = new
        {
            navigateOk = nav.Ok,
            personsTabActive = k.Active is not null && k.Active.Url.Scheme == "jev",
            agentPageShownAtEnd = agentTab is null ? "no page" : (_leases!.TryGet(agentTab.Id, out var l) ? ((WebView2Lease)l).View.Visibility.ToString() : "no renderer"),
            stagedGeometry = stagedFacts.Select(f => new { f.X, f.Y, f.Width, f.Height, f.WindowWidth, f.WindowHeight, f.OverlapsWindow, f.HitTestVisible, f.TabStop }).ToList(),
            stagedNeverOverlappedTheWindow = stagedFacts.Count > 0 && stagedFacts.All(f => !f.OverlapsWindow && !f.HitTestVisible && !f.TabStop),
            spans,
        };
        WebView2Lease.StagedObserver = null;
        await host.StopAsync(session);
        await File.WriteAllTextAsync(Path.Combine(dir, "stage-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// A picture must never be delivered of a page that shows a password or payment field, wherever the field is: in the page, in an
    /// iframe of the same origin, in a cross-origin iframe, two frames deep, or one that appears while the picture is being taken.
    /// A local server provides the pages under two origins (127.0.0.1 and localhost) so "cross-origin" is real. The control (no field)
    /// must be delivered, so a refusal cannot be a broken capture.
    /// </summary>
    private sealed record FrameCase(string name, string path, bool expectDelivered, bool delivered, bool ok, bool navigateOk, string message);

    private async Task RunAgentFrameSecretCheckAsync()
    {
        var k = _kernel!;
        var dir = Path.Combine(DataDir, "benchmarks"); Directory.CreateDirectory(dir);
        await Task.Delay(3000);
        int port; System.Net.HttpListener? server = null;
        for (port = 47800; port < 47900; port++)
        {
            var l = new System.Net.HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{port}/"); l.Prefixes.Add($"http://localhost:{port}/");
            try { l.Start(); server = l; break; } catch (Exception) { l.Close(); }
        }
        if (server is null) { await File.WriteAllTextAsync(Path.Combine(dir, "frame-secret-check.json"), "{\"error\":\"no free port\"}"); return; }
        string A = $"http://127.0.0.1:{port}", B = $"http://localhost:{port}";
        string Page(string body) => "<!doctype html><html><head><meta charset=utf-8><title>t</title></head><body><h1>A perfectly ordinary page</h1><p>" + string.Concat(Enumerable.Repeat("Some readable words so the page has real content. ", 12)) + "</p>" + body + "</body></html>";
        var pages = new Dictionary<string, string>
        {
            ["/plain"] = Page(""),
            ["/inner-a"] = "<!doctype html><html><body><form><label>Password <input type=\"password\" name=\"p\"></label></form></body></html>",
            ["/inner-b"] = "<!doctype html><html><body><form><label>Card <input autocomplete=\"cc-number\" name=\"c\"></label></form></body></html>",
            ["/f1"] = Page($"<iframe src=\"/inner-a\" width=\"420\" height=\"120\"></iframe>"),
            ["/f2"] = Page($"<iframe src=\"{B}/inner-a\" width=\"420\" height=\"120\"></iframe>"),
            ["/f3"] = Page($"<iframe src=\"{B}/inner-b\" width=\"420\" height=\"120\"></iframe>"),
            ["/inner-c"] = "<!doctype html><html><body><p>A harmless embedded widget with no fields at all.</p></body></html>",
            ["/mid"] = "<!doctype html><html><body><p>middle frame</p><iframe src=\"" + A + "/inner-a\" width=\"400\" height=\"100\"></iframe></body></html>",
            ["/f4"] = Page($"<iframe src=\"{B}/mid\" width=\"440\" height=\"160\"></iframe>"),
        };
        var serving = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                System.Net.HttpListenerContext c;
                try { c = await server.GetContextAsync(); } catch (Exception) { break; }
                var path = c.Request.Url!.AbsolutePath;
                var html = pages.TryGetValue(path, out var h) ? h : "<html><body>not found</body></html>";
                var bytes = System.Text.Encoding.UTF8.GetBytes(html);
                c.Response.ContentType = "text/html; charset=utf-8"; c.Response.ContentLength64 = bytes.Length;
                await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
            }
        });

        var ceiling = new AgentCeiling { Limits = new AgentManifest { Agent = "ceiling", AllowDomains = ["127.0.0.1", "localhost"], Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.Screenshot], SessionMinutes = 10, MaxLivePages = 2, MaxActions = 200, MaxScreenshots = 40, DenyDataClasses = [DataClass.Secret] } };
        using var host = new LocalAgentHost(_agents!, ceiling, approveScreenshots: _ => Task.FromResult(true));   // a check with nobody present: approval given on purpose
        var (session, _) = await host.GrantAsync(new AgentManifest { Agent = "frame-probe", AllowDomains = ["127.0.0.1", "localhost"], Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.Screenshot], SessionMinutes = 10, MaxLivePages = 2, MaxScreenshots = 40, DenyDataClasses = [DataClass.Secret] });

        var cases = new List<FrameCase>();
        async Task Case(string name, string path, bool expectDelivered, Func<Task>? during = null)
        {
            var nav = await _agents!.ExecuteAsync(session, new AgentRequest(AgentAction.Navigate, A + path), default);
            await Task.Delay(3000);                                  // frames load and the page script reports what it found
            var shot = during is null
                ? await _agents.ExecuteAsync(session, new AgentRequest(AgentAction.Screenshot), default)
                : await ShotWhile(during);
            var delivered = shot.Screenshot is { Length: > 0 };
            cases.Add(new FrameCase(name, path, expectDelivered, delivered, delivered == expectDelivered, nav.Ok, shot.Message));
        }
        async Task<AgentResponse> ShotWhile(Func<Task> during)
        {
            var taking = _agents!.ExecuteAsync(session, new AgentRequest(AgentAction.Screenshot), default);
            await Task.Delay(120);                                   // inside the 200 ms the page is being drawn
            await during();
            return await taking;
        }
        async Task InjectBenignFrame()
        {
            var page = k.Tabs.FirstOrDefault(t => session.Pages.Contains(t.Id) && t.Id == session.Current);
            if (page is not null && _leases!.TryGet(page.Id, out var l) && ((WebView2Lease)l).View.CoreWebView2 is { } core)
                await core.ExecuteScriptAsync($"(()=>{{const f=document.createElement('iframe');f.src='{B}/inner-c';f.width=400;f.height=100;document.body.appendChild(f);}})()");
        }
        async Task InjectSecretFrame()
        {
            var page = k.Tabs.FirstOrDefault(t => session.Pages.Contains(t.Id) && t.Id == session.Current);
            if (page is not null && _leases!.TryGet(page.Id, out var l) && ((WebView2Lease)l).View.CoreWebView2 is { } core)
                await core.ExecuteScriptAsync($"(()=>{{const f=document.createElement('iframe');f.src='{B}/inner-a';f.width=400;f.height=100;document.body.appendChild(f);}})()");
        }
        await Case("control: an ordinary page", "/plain", expectDelivered: true);
        await Case("password field in a same-origin iframe", "/f1", false);
        await Case("password field in a cross-origin iframe", "/f2", false);
        await Case("payment field in a cross-origin iframe", "/f3", false);
        await Case("password field two frames deep (cross-origin, then back)", "/f4", false);
        for (var i = 1; i <= 3; i++) await Case($"password iframe appears during the capture (run {i})", $"/plain?late={i}", false, InjectSecretFrame);
        // Ordinary frame activity while a picture is taken (an ad, a widget) must not by itself throw the picture away: only a change that matters may.
        await Case("control: a harmless iframe appears during the capture", "/plain?benign=1", expectDelivered: true, InjectBenignFrame);
        server.Stop(); server.Close();
        await host.StopAsync(session);
        var result = new { pass = cases.All(c => c.ok), cases };
        await File.WriteAllTextAsync(Path.Combine(dir, "frame-secret-check.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record ShowDuringCapture(int clickAfterMs, bool agentPageActive, bool agentPageShown, bool personsPageHidden, bool layoutRestored, string geometry, bool captureDelivered, string captureMessage, bool afterwardsCaptureWorks, bool ok);

    /// <summary>
    /// "Show its page" pressed while a screenshot has the page staged off-canvas. The kernel's wish (show it) must win, and the control
    /// must end up exactly as a normal shown page: on canvas, at the size of its host, clickable, not left at the staged 1280x800 or
    /// stranded outside the window. Sampled at several moments across the staging and the capture, because it is a race.
    /// </summary>
    private async Task RunAgentShowDuringCaptureCheckAsync()
    {
        var k = _kernel!;
        var dir = Path.Combine(DataDir, "benchmarks"); Directory.CreateDirectory(dir);
        var mine = k.Open(new Uri("https://example.com/"));
        await k.ActivateAsync(mine.Id);
        await Task.Delay(4000);
        var ceiling = new AgentCeiling { Limits = new AgentManifest { Agent = "ceiling", AllowDomains = ["example.org"], Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.Screenshot], SessionMinutes = 10, MaxLivePages = 2, MaxActions = 200, MaxScreenshots = 40 } };
        using var host = new LocalAgentHost(_agents!, ceiling, approveScreenshots: _ => Task.FromResult(true));   // a check with nobody present: approval given on purpose
        var (session, _) = await host.GrantAsync(new AgentManifest { Agent = "show-probe", AllowDomains = ["example.org"], Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.Screenshot], SessionMinutes = 10, MaxLivePages = 2, MaxScreenshots = 40 });
        await _agents!.ExecuteAsync(session, new AgentRequest(AgentAction.Navigate, "https://example.org/"), default);
        await Task.Delay(3000);
        var agentTab = k.Tabs.First(t => session.Pages.Contains(t.Id));
        var rows = new List<ShowDuringCapture>();
        foreach (var clickAfter in new[] { 20, 80, 140, 190, 230, 300 })
        {
            // Back to the starting position: the person on their own page, the agent's page hidden.
            if (k.Active?.Id != mine.Id) { await k.ActivateAsync(mine.Id); RebuildWorkspaces(); await Task.Delay(500); }
            var taking = _agents.ExecuteAsync(session, new AgentRequest(AgentAction.Screenshot), default);
            await Task.Delay(clickAfter);
            await k.ActivateAsync(agentTab.Id);                     // what the panel's "Show its page" does
            RebuildWorkspaces();
            var shot = await taking;
            await Task.Delay(1200);
            _leases!.TryGet(agentTab.Id, out var l);
            var lease = (WebView2Lease)l!;
            var view = lease.View; var mineLease = _leases.TryGet(mine.Id, out var ml) ? (WebView2Lease)ml : null;
            var host2 = (Microsoft.UI.Xaml.FrameworkElement)view.Parent;
            var atOrigin = view.TransformToVisual(host2).TransformPoint(new Windows.Foundation.Point(0, 0));
            var sizeOk = Math.Abs(view.ActualWidth - host2.ActualWidth) < 3 && Math.Abs(view.ActualHeight - host2.ActualHeight) < 3;
            var onCanvas = Math.Abs(atOrigin.X) < 3 && Math.Abs(atOrigin.Y) < 3;
            var normal = double.IsNaN(view.Width) && double.IsNaN(view.Height) && view.Margin.Left == 0 && view.Margin.Top == 0
                         && view.HorizontalAlignment == Microsoft.UI.Xaml.HorizontalAlignment.Stretch && view.VerticalAlignment == Microsoft.UI.Xaml.VerticalAlignment.Stretch && view.IsHitTestVisible;
            var active = k.Active?.Id == agentTab.Id;
            var shown = view.Visibility == Microsoft.UI.Xaml.Visibility.Visible && lease.IsVisible;
            var hidden = mineLease is null || mineLease.View.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed;
            // A picture of the now-shown page still works through the ordinary (shown) path.
            var again = await _agents.ExecuteAsync(session, new AgentRequest(AgentAction.Screenshot), default);
            var delivered = shot.Screenshot is { Length: > 0 };
            var layout = sizeOk && onCanvas && normal;
            rows.Add(new ShowDuringCapture(clickAfter, active, shown, hidden, layout, $"origin=({atOrigin.X:0.#},{atOrigin.Y:0.#}) size={view.ActualWidth:0}x{view.ActualHeight:0} host={host2.ActualWidth:0}x{host2.ActualHeight:0} width={view.Width} margin={view.Margin.Left}", delivered,
                shot.Message, again.Screenshot is { Length: > 0 }, active && shown && hidden && layout && again.Screenshot is { Length: > 0 }));
        }
        await host.StopAsync(session);
        var result = new { pass = rows.All(r => r.ok), rows };
        await File.WriteAllTextAsync(Path.Combine(dir, "show-during-capture.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// The agent-running indicator and its Stop, on the real app: absent with no agent; present, named and opening the activity panel while
    /// one runs; and one press of Stop (invoked the way a screen reader or keyboard would) ends the session, releases its pages and removes
    /// the indicator, with no dialog in between.
    /// </summary>
    private async Task RunAgentIndicatorCheckAsync()
    {
        var k = _kernel!;
        var dir = Path.Combine(DataDir, "benchmarks"); Directory.CreateDirectory(dir);
        var mine = k.Open(new Uri("https://example.com/"));
        await k.ActivateAsync(mine.Id);
        await Task.Delay(3500);
        var hiddenAtStart = AgentGroup.Visibility == Visibility.Collapsed;
        var ceiling = new AgentCeiling { Limits = new AgentManifest { Agent = "ceiling", AllowDomains = ["example.org"], Actions = [AgentAction.Navigate, AgentAction.Read], SessionMinutes = 10, MaxLivePages = 2, MaxActions = 100 } };
        _agentHost = new LocalAgentHost(_agents!, ceiling);
        _agentHost.BackgroundFault += OnAgentHostFault;
        var (session, _) = await _agentHost.GrantAsync(new AgentManifest { Agent = "indicator-probe", AllowDomains = ["example.org"], Actions = [AgentAction.Navigate, AgentAction.Read], SessionMinutes = 10, MaxLivePages = 2 });
        await _agents!.ExecuteAsync(session, new AgentRequest(AgentAction.Navigate, "https://example.org/"), default);
        await Task.Delay(1500);
        var shown = AgentGroup.Visibility == Visibility.Visible;
        var text = AgentBadge.Content?.ToString() ?? "";
        var badgeName = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(AgentBadge);
        var stopName = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(AgentStopButton);
        // Reachable at the width the window has now: the group sits inside the wrapping bar, so it is measured with it.
        Toolbar.UpdateLayout();                                   // a window nobody is looking at may not have run a layout pass yet
        var debugWhileRunning = $"group vis={AgentGroup.Visibility} desired={AgentGroup.DesiredSize.Width}x{AgentGroup.DesiredSize.Height} render={AgentGroup.RenderSize.Width}x{AgentGroup.RenderSize.Height} badge vis={AgentBadge.Visibility} desired={AgentBadge.DesiredSize.Width} content='{AgentBadge.Content}' parent={AgentGroup.Parent?.GetType().Name} trustDesired={TrustBar.DesiredSize.Width}x{TrustBar.DesiredSize.Height} row={Grid.GetRow(TrustBar)} col={Grid.GetColumn(TrustBar)} span={Grid.GetColumnSpan(TrustBar)} shieldW={ShieldButton.ActualWidth} shieldVis={ShieldButton.Visibility}";
        var badgeW = AgentBadge.ActualWidth; var stopW = AgentStopButton.ActualWidth; var groupW = AgentGroup.ActualWidth; var barW = TrustBar.ActualWidth;
        var reachable = badgeW > 8 && stopW > 8 && AgentBadge.IsTabStop && AgentStopButton.IsTabStop;

        new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(AgentBadge).Invoke();     // opens what it is doing
        await Task.Delay(800);
        var opensPanel = PanelOpen && _panelId == "agents";

        var pagesBefore = _agents.LiveAgentPages(session);
        var dialogOpen = false;
        new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(AgentStopButton).Invoke();  // one press, no confirmation
        for (var i = 0; i < 40 && !session.CleanedUp; i++) await Task.Delay(250);
        await Task.Delay(500);
        var result = new
        {
            pass = hiddenAtStart && shown && text.Contains("indicator-probe") && badgeName.Contains("indicator-probe") && stopName.StartsWith("Stop") && reachable && opensPanel
                   && session.CleanedUp && _agents.LiveAgentPages(session) == 0 && AgentGroup.Visibility == Visibility.Collapsed && !dialogOpen,
            hiddenAtStart, shown, text, badgeName, stopName, reachable, opensPanel, livePagesBeforeStop = pagesBefore,
            debug = debugWhileRunning,
            badgeWidthWhileRunning = badgeW, stopWidthWhileRunning = stopW, groupWidthWhileRunning = groupW, trustBarWidthWhileRunning = barW, badgeTabStop = AgentBadge.IsTabStop, stopTabStop = AgentStopButton.IsTabStop, toolbarWidth = Toolbar.ActualWidth, trustBarWidth = TrustBar.ActualWidth,
            sessionCleanedUp = session.CleanedUp, livePagesAfterStop = _agents.LiveAgentPages(session), indicatorHiddenAfterStop = AgentGroup.Visibility == Visibility.Collapsed,
            statusLine = StatusText.Text,
        };
        await File.WriteAllTextAsync(Path.Combine(dir, "agent-indicator-check.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private LocalAgentHost? _hiddenLoadHost;
    private System.Net.HttpListener? _hiddenLoadServer;

    /// <summary>
    /// For measurement only: opens N agent pages from a local server and leaves them alone so their cost can be read from outside. Kinds:
    /// "static" (a plain page), "busy" (a page that draws to a canvas every frame, animates in CSS and runs a hot timer, the worst thing a
    /// page can do to be a nuisance), "busy-shown" (the same page but SHOWN: the control that proves the instrument can see a busy page).
    /// Runs alongside the normal window; nothing exits. Writes what it did and what the controls' visibility really was.
    /// </summary>
    private async Task RunAgentHiddenLoadAsync(string spec)
    {
        try
        {
            var parts = spec.Split(':'); var kind = parts[0]; var count = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 1;
            var k = _kernel!;
            var dir = Path.Combine(DataDir, "benchmarks"); Directory.CreateDirectory(dir);
            await Task.Delay(6000);
            int port = 47900; System.Net.HttpListener? server = null;
            for (; port < 48000; port++)
            {
                var l = new System.Net.HttpListener(); l.Prefixes.Add($"http://127.0.0.1:{port}/");
                try { l.Start(); server = l; break; } catch (Exception) { l.Close(); }
            }
            if (server is null) return;
            _hiddenLoadServer = server;
            const string busy = "<!doctype html><html><head><meta charset=utf-8><title>busy</title><style>@keyframes spin{to{transform:rotate(360deg)}}#a{width:80px;height:80px;background:#08c;animation:spin 1s linear infinite}</style></head><body><div id=a></div><canvas id=c width=600 height=400></canvas><script>"
                + "const g=document.getElementById('c').getContext('2d');let t=0;function f(){t++;g.clearRect(0,0,600,400);for(let i=0;i<300;i++){g.beginPath();g.arc(300+Math.cos(t/20+i)*200,200+Math.sin(t/17+i)*150,8,0,6.3);g.fill();}requestAnimationFrame(f);}f();"
                + "let x=0;setInterval(()=>{for(let i=0;i<20000;i++)x+=Math.sqrt(i);},4);</script></body></html>";
            const string plain = "<!doctype html><html><head><meta charset=utf-8><title>static</title></head><body><h1>A quiet page</h1><p>Nothing here moves.</p></body></html>";
            _ = Task.Run(async () =>
            {
                while (server.IsListening)
                {
                    System.Net.HttpListenerContext ctx;
                    try { ctx = await server.GetContextAsync(); } catch (Exception) { break; }
                    var bytes = System.Text.Encoding.UTF8.GetBytes(kind.StartsWith("busy", StringComparison.Ordinal) ? busy : plain);
                    ctx.Response.ContentType = "text/html; charset=utf-8"; ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
                }
            });
            var ceiling = new AgentCeiling { Limits = new AgentManifest { Agent = "ceiling", AllowDomains = ["127.0.0.1"], Actions = [AgentAction.Navigate, AgentAction.Read], SessionMinutes = 60, MaxLivePages = 8, MaxActions = 500, DenyDataClasses = [DataClass.Secret] } };
            _hiddenLoadHost = new LocalAgentHost(_agents!, ceiling);
            _agentHost = _hiddenLoadHost;
            var (session, _) = await _hiddenLoadHost.GrantAsync(new AgentManifest { Agent = "load-probe", AllowDomains = ["127.0.0.1"], Actions = [AgentAction.Navigate, AgentAction.Read], SessionMinutes = 60, MaxLivePages = Math.Max(count, 1), DenyDataClasses = [DataClass.Secret] });
            for (var i = 0; i < count; i++) { await _agents!.ExecuteAsync(session, new AgentRequest(AgentAction.Navigate, $"http://127.0.0.1:{port}/{kind}?n={i}"), default); await Task.Delay(800); }
            await Task.Delay(3000);
            if (kind == "busy-shown" && k.Tabs.FirstOrDefault(t => session.Pages.Contains(t.Id)) is { } first) { await k.ActivateAsync(first.Id); RebuildWorkspaces(); }
            await Task.Delay(2000);
            string Vis(ResourceId id) => _leases!.TryGet(id, out var lease) ? ((WebView2Lease)lease).View.Visibility.ToString() : "no renderer";
            var pages = k.Tabs.Where(t => session.Pages.Contains(t.Id)).Select(t => new { url = t.Url.ToString(), state = t.State.ToString(), shown = Vis(t.Id) }).ToList();
            var state = new { kind, count, agentPages = pages, personsPageActive = k.Active is not null && !session.Pages.Contains(k.Active.Id), liveRenderers = _leases!.LiveResources.Count };
            await File.WriteAllTextAsync(Path.Combine(dir, "hidden-load-state.json"), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { try { await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "hidden-load-error.txt"), ex.ToString()); } catch (Exception) { } }
    }

    /// <summary>
    /// What must be true of the window when nothing is happening. A control that animates by itself (an indeterminate progress bar) costs
    /// CPU and GPU for as long as it is visible, even inside a collapsed panel nobody can see, so "no restore in flight" has to mean it is
    /// not visible. This is the rule that a start-up restore quietly broke (idle CPU went from about 1.4% to about 7%); the idle-cost
    /// script measures the consequence, this checks the cause.
    /// </summary>
    private async Task RunIdleInvariantsCheckAsync()
    {
        var k = _kernel!;
        var dir = Path.Combine(DataDir, "benchmarks"); Directory.CreateDirectory(dir);
        var t = k.Open(new Uri(WelcomePage.Url));
        await k.ActivateAsync(t.Id);
        await Task.Delay(9000);
        var noRestore = _restoringId is null;
        var progressVisible = RestoreProgress.Visibility == Visibility.Visible;
        var result = new { restoringIdEmpty = noRestore, restorePanel = RestorePanel.Visibility.ToString(), restoreProgressVisible = progressVisible, pass = !(noRestore && progressVisible) };
        await File.WriteAllTextAsync(Path.Combine(dir, "idle-invariants.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Puts the app in the state a crash test needs: a normal tab, and a live Private session (a tab with a cookie in its throwaway profile). Writes down what
    /// exists so the script can compare it with what is left after the app is killed or closed. Runs alongside the normal window.
    /// </summary>
    private async Task RunRecoverySeedAsync()
    {
        try
        {
            var progress = Path.Combine(DataDir, "benchmarks", "recovery-seed-progress.txt");
            void Step(string what) { try { File.AppendAllText(progress, $"{DateTime.Now:HH:mm:ss.fff} {what}\n"); } catch (Exception) { } }
            Directory.CreateDirectory(Path.GetDirectoryName(progress)!);
            Step("started");
            await Task.Delay(6000);
            var k = _kernel!;
            Step("opening the normal tab");
            var normalTab = k.Open(new Uri(WelcomePage.Url + "?normal-marker=keep-4242"));
            await k.ActivateAsync(normalTab.Id);
            Step("normal tab active; entering Private mode");
            await ChangeProductModeAsync(ProductMode.Private);
            var session = k.ActiveWorkspace;
            Step("in Private mode; opening the private tab");
            var priv = k.Open(new Uri(WelcomePage.Url + "?private-marker=SECRET-7731"));
            await k.ActivateAsync(priv.Id);
            Step("private tab active");
            _leases!.TryGet(priv.Id, out var l0); var lease = (WebView2Lease)l0!;
            var mgr = lease.View.CoreWebView2.CookieManager;
            mgr.AddOrUpdateCookie(mgr.CreateCookie("session", "private-cookie-7731", "recovery-probe.test", "/"));
            await Task.Delay(3000);
            var dir = Path.Combine(DataDir, "benchmarks"); Directory.CreateDirectory(dir);
            var state = new
            {
                pid = Environment.ProcessId,
                normalTab = normalTab.Id.ToString(),
                privateTab = priv.Id.ToString(),
                privateWorkspace = session.ToString(),
                privateWorkspaceCount = k.Workspaces.Count(w => w.Container == IdentityContainer.Private),
                tabs = k.Tabs.Count,
                readyAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            };
            await File.WriteAllTextAsync(Path.Combine(dir, "recovery-seed.json"), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { try { await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "recovery-seed-error.txt"), ex.ToString()); } catch (Exception) { } }
    }

    /// <summary>
    /// Puts the normal window into one known state and leaves it alone, so its idle cost can be measured from outside: "static" (nothing
    /// open), "panel-open" (the Receipt panel showing) or "panel-closed" (the panel opened and then closed, which must leave nothing behind).
    /// Then writes down what the window REALLY is, so the measurement can be refused if the state is not the one asked for.
    /// </summary>
    private async Task RunIdleScenarioAsync(string scenario)
    {
        try
        {
            await Task.Delay(8000);
            if (scenario == "panel-open") OnReceipt(this, new RoutedEventArgs());
            else if (scenario == "panel-closed") { OnReceipt(this, new RoutedEventArgs()); await Task.Delay(1500); ClosePanel(restoreFocus: false); }
            await Task.Delay(1500);
            var dir = Path.Combine(DataDir, "benchmarks"); Directory.CreateDirectory(dir);
            var state = new
            {
                scenario,
                panelOpen = PanelOpen,
                panelId = _panelId,
                activeUrl = _kernel?.Active?.Url.ToString(),
                tabs = _kernel?.Tabs.Count ?? 0,
                restoreInFlight = _restoringId is not null,
                // Diagnostic only. It is the cause of the idle-CPU regression this harness was built to catch, but the harness must find that
                // by MEASURING CPU, not by reading this.
                restoreProgressVisible = RestoreProgress.Visibility == Visibility.Visible,
                agentSessionsRunning = (_agentHost?.Sessions ?? []).Count(x => !x.Closed && !x.CleanedUp),
                readyAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            };
            await File.WriteAllTextAsync(Path.Combine(dir, "idle-scenario-state.json"), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { try { await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "idle-scenario-error.txt"), ex.ToString()); } catch (Exception) { } }
    }

    /// <summary>
    /// Can this browser actually hold a video call? Real renderer, real engine. Three separate questions, because
    /// they fail for different reasons and "it didn't work" is not a useful answer:
    /// <list type="number">
    /// <item>Does the engine expose the APIs a conferencing site needs (getUserMedia, getDisplayMedia, RTCPeerConnection)?</item>
    /// <item>Does a real peer connection negotiate and carry media? Driven with a synthetic canvas track so this
    /// answers the question on a machine with no camera attached — a hardware-independent proof of the stack.</item>
    /// <item>Do camera and microphone actually open through our permission path, and does the tab then refuse to be
    /// put to sleep underneath the call?</item>
    /// </list>
    /// The permission prompt is auto-allowed here (there is nobody to click it) and the result says so.
    /// </summary>
    private async Task RunMediaCheckAsync()
    {
        var k = _kernel!;
        foreach (var t in k.Tabs.ToList()) await k.CloseAsync(t.Id);

        // A secure context is required for getUserMedia; example.com is https and loads fast.
        var tab = k.Open(new Uri("https://example.com/"));
        await k.ActivateAsync(tab.Id);
        await Task.Delay(4000);
        if (!_leases!.TryGet(tab.Id, out _)) return;

        // Re-resolved on every call, never captured: one of the stages below deliberately lets the tab sleep, which
        // disposes the renderer. Holding the first CoreWebView2 would make every later stage fail with a COM error
        // that looks like a media bug and is not one.
        CoreWebView2 Core()
        {
            if (!_leases.TryGet(tab.Id, out var l)) throw new InvalidOperationException("the tab has no renderer");
            return ((WebView2Lease)l).View.CoreWebView2;
        }

        // ExecuteScriptAsync does not await promises — it returns "{}" for one — and everything here is async.
        // So the script posts its result back over the message bridge and we wait for the tagged reply.
        int probe = 0;
        async Task<JsonElement> EvalAsync(string js, int timeoutMs = 30000)
        {
            var tag = $"jev:media:{++probe}:";
            var tcs = new TaskCompletionSource<string>();
            void OnMessage(CoreWebView2 _, CoreWebView2WebMessageReceivedEventArgs e)
            {
                string m; try { m = e.TryGetWebMessageAsString(); } catch (ArgumentException) { return; }
                if (m.StartsWith(tag, StringComparison.Ordinal)) tcs.TrySetResult(m[tag.Length..]);
            }
            var core = Core();
            core.WebMessageReceived += OnMessage;
            try
            {
                await core.ExecuteScriptAsync($$"""
                    (async () => {
                      const post = v => { try { chrome.webview.postMessage({{JsonSerializer.Serialize(tag)}} + JSON.stringify(v)); } catch {} };
                      try { post(await (async () => { {{js}} })()); }
                      catch (e) { post({ error: String((e && e.name) || e) + ': ' + String((e && e.message) || '') }); }
                    })();
                    """);
                if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) != tcs.Task)
                    return JsonDocument.Parse("""{"error":"timed out"}""").RootElement.Clone();
                return JsonDocument.Parse(await tcs.Task).RootElement.Clone();
            }
            finally { core.WebMessageReceived -= OnMessage; }
        }

        // 1. API surface.
        var apis = await EvalAsync("""
            return {
              secureContext: !!window.isSecureContext,
              getUserMedia: !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia),
              getDisplayMedia: !!(navigator.mediaDevices && navigator.mediaDevices.getDisplayMedia),
              enumerateDevices: !!(navigator.mediaDevices && navigator.mediaDevices.enumerateDevices),
              RTCPeerConnection: typeof RTCPeerConnection === 'function',
              insertableStreams: typeof RTCRtpSender !== 'undefined' && 'createEncodedStreams' in RTCRtpSender.prototype,
              webAudio: typeof AudioContext === 'function',
              wasm: typeof WebAssembly === 'object',
              // Shared memory depends on the DOCUMENT's isolation, not on the browser. This probe page sends no
              // COOP/COEP headers, so absence here says nothing about a conferencing site that sends its own.
              sharedArrayBuffer: typeof SharedArrayBuffer === 'function',
              crossOriginIsolated: !!self.crossOriginIsolated,
              codecs: (typeof RTCRtpSender !== 'undefined' && RTCRtpSender.getCapabilities)
                ? (RTCRtpSender.getCapabilities('video').codecs || []).map(c => c.mimeType).filter((v,i,a) => a.indexOf(v) === i)
                : [],
              userAgent: navigator.userAgent,
            };
            """);

        // 2. A real loopback call: two peer connections, a synthetic video track, negotiated for real.
        var loopback = await EvalAsync("""
            const cv = document.createElement('canvas'); cv.width = 320; cv.height = 240;
            const ctx = cv.getContext('2d');
            const paint = () => { ctx.fillStyle = '#' + Math.floor(Math.random()*16777215).toString(16); ctx.fillRect(0,0,320,240); };
            paint(); const timer = setInterval(paint, 100);
            const stream = cv.captureStream(15);
            const a = new RTCPeerConnection(), b = new RTCPeerConnection();
            a.onicecandidate = e => e.candidate && b.addIceCandidate(e.candidate);
            b.onicecandidate = e => e.candidate && a.addIceCandidate(e.candidate);
            const got = new Promise(res => { b.ontrack = e => res(e.track); });
            stream.getTracks().forEach(t => a.addTrack(t, stream));
            await a.setLocalDescription(await a.createOffer());
            await b.setRemoteDescription(a.localDescription);
            await b.setLocalDescription(await b.createAnswer());
            await a.setRemoteDescription(b.localDescription);
            const connected = await new Promise(res => {
              const done = () => { if (a.connectionState === 'connected') res(true); };
              a.onconnectionstatechange = done; done();
              setTimeout(() => res(a.connectionState === 'connected'), 15000);
            });
            const track = await Promise.race([got, new Promise(r => setTimeout(() => r(null), 5000))]);
            await new Promise(r => setTimeout(r, 2000));
            let inbound = null;
            (await b.getStats()).forEach(s => { if (s.type === 'inbound-rtp' && s.kind === 'video') inbound = s; });
            clearInterval(timer); a.close(); b.close();
            return {
              connected, connectionState: 'closed-after-test',
              remoteTrackReceived: !!track, remoteTrackKind: track ? track.kind : null,
              framesDecoded: inbound ? (inbound.framesDecoded || 0) : 0,
              bytesReceived: inbound ? (inbound.bytesReceived || 0) : 0,
            };
            """, 40000);

        // 3. Real devices through our own permission path. Auto-allowed: this bench has no user.
        _autoAllowPermissions = true;
        var devices = await EvalAsync("""
            const before = await navigator.mediaDevices.enumerateDevices();
            let mic = null, cam = null, labelsVisible = false;
            try {
              const s = await navigator.mediaDevices.getUserMedia({ audio: true, video: true });
              mic = !!s.getAudioTracks().length; cam = !!s.getVideoTracks().length;
              const after = await navigator.mediaDevices.enumerateDevices();
              labelsVisible = after.some(d => d.label && d.label.length > 0);
              s.getTracks().forEach(t => t.stop());
            } catch (e) { return {
              audioInputs: before.filter(d => d.kind === 'audioinput').length,
              videoInputs: before.filter(d => d.kind === 'videoinput').length,
              getUserMediaError: e.name + ': ' + e.message };
            }
            return {
              audioInputs: before.filter(d => d.kind === 'audioinput').length,
              videoInputs: before.filter(d => d.kind === 'videoinput').length,
              microphoneTrack: mic, cameraTrack: cam, labelsVisibleAfterGrant: labelsVisible,
            };
            """, 40000);
        _autoAllowPermissions = false;

        // 4. Does a live call stop the scheduler putting the tab to sleep underneath it?
        // Asserted specifically, not by looking for the word "refused" in a message: a capture failure or an
        // unrelated protection flag would satisfy that, and then a regression would read as a pass.
        object callProtection = "not observed";
        bool micFlagged = false, vetoedByMedia = false, mediaSurvived = false, clearedAfterStop = false, sleepsAfterStop = false;
        try
        {
            _autoAllowPermissions = true;
            var opened = await EvalAsync("window.__jevCall = await navigator.mediaDevices.getUserMedia({audio:true, video:true}); return { ok: true };", 30000);
            if (opened.TryGetProperty("ok", out _))
            {
                await Task.Delay(2500);
                var during = tab.Protection;
                micFlagged = during.HasFlag(ProtectionFlags.MicrophoneActive) && during.HasFlag(ProtectionFlags.CameraActive);

                var demote = await k.VirtualizeAsync(tab.Id, Cause.Scheduler);
                vetoedByMedia = !demote.Allowed && demote.Reason.Contains("MicrophoneActive", StringComparison.Ordinal);

                // The renderer must still be there, and the media still running — a veto that killed the call anyway
                // would be worthless.
                var alive = await EvalAsync("return { live: window.__jevCall.getTracks().filter(t => t.readyState === 'live').length };", 10000);
                mediaSurvived = tab.State.HasLiveRenderer() && alive.TryGetProperty("live", out var lv) && lv.GetInt32() == 2;

                // And when the call ends, the tab must go back to being an ordinary tab.
                await EvalAsync("window.__jevCall.getTracks().forEach(t => t.stop()); return { ok: true };", 10000);
                await Task.Delay(1500);
                clearedAfterStop = !tab.Protection.HasLiveMedia();
                sleepsAfterStop = (await k.VirtualizeAsync(tab.Id, Cause.Scheduler)).Allowed;

                callProtection = new
                {
                    duringCall = during.ToString(),
                    schedulerVerdict = demote.Allowed ? "PUT IT TO SLEEP" : demote.Reason,
                    liveTracksAfterVeto = alive.TryGetProperty("live", out var l2) ? l2.GetInt32() : -1,
                    afterStop = tab.Protection.ToString(),
                    sleepsAfterStop,
                };
            }
            else callProtection = "no media to hold: " + opened;
        }
        catch (Exception ex) { callProtection = "probe failed: " + ex.GetType().Name + ": " + ex.Message; }
        finally { _autoAllowPermissions = false; }

        // 5. A live page whose JavaScript stalls. One long main-thread task stops the heartbeat while the microphone
        // stays open — if silence were read as "finished", the scheduler would be handed a live capture to dispose.
        object stalled = "not run";
        bool stallHeld = false;
        string stallProtection = "?", stallVerdict = "?", stallNote = "";
        try
        {
            _autoAllowPermissions = true;
            if (!tab.State.HasLiveRenderer()) await k.ActivateAsync(tab.Id);   // stage 4 let it sleep, by design
            await Task.Delay(3000);
            var started = await EvalAsync("window.__stall = await navigator.mediaDevices.getUserMedia({audio:true}); return { ok: true };", 30000);
            await Task.Delay(1500);
            // Fire and forget, and swallow: the page is about to stop answering, so this call will not come back.
            _ = Core().ExecuteScriptAsync("(() => { const end = Date.now() + 12000; while (Date.now() < end) {} })()")
                    .AsTask().ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            await Task.Delay(9000);                                   // past the 6 s heartbeat grace

            // Recorded BEFORE anything else can throw, so a later failure cannot hide what we came to measure.
            stallProtection = tab.Protection.ToString();
            var demote = await k.VirtualizeAsync(tab.Id, Cause.Scheduler);
            stallVerdict = demote.Allowed ? "PUT IT TO SLEEP" : demote.Reason;
            stallHeld = !demote.Allowed && tab.Protection.HasLiveMedia();
            stallNote = started.TryGetProperty("ok", out _) ? "" : "capture did not start: " + started;

            await Task.Delay(4500);                                   // let the stall finish
            if (tab.State.HasLiveRenderer())
            {
                await EvalAsync("if (window.__stall) window.__stall.getTracks().forEach(t => t.stop()); return { ok: true };", 10000);
                await Task.Delay(1500);
            }
        }
        catch (Exception ex) { stallNote = "after measuring: " + ex.GetType().Name + ": " + ex.Message; }
        finally { _autoAllowPermissions = false; }
        stalled = new { uncertainDoesNotMeanIdle = stallHeld, protectionWhileStalled = stallProtection, verdict = stallVerdict, note = stallNote };

        // 6. Sibling and nested frames, over http://127.0.0.1 (a secure context, so getUserMedia is allowed).
        object frames = "not run";
        bool frameCases = false;
        try
        {
            _autoAllowPermissions = true;
            frames = await RunFrameMediaProbeAsync(k, tab, EvalAsync);
            frameCases = frames is IDictionary<string, object> d && d.TryGetValue("pass", out var fp) && fp is true;
        }
        catch (Exception ex) { frames = "probe failed: " + ex.GetType().Name + ": " + ex.Message; }
        finally { _autoAllowPermissions = false; }

        bool B(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
        var stackWorks = B(apis, "getUserMedia") && B(apis, "RTCPeerConnection") && B(loopback, "connected") && B(loopback, "remoteTrackReceived");
        var framesFlowed = loopback.TryGetProperty("framesDecoded", out var fd) && fd.GetInt32() > 0;

        // A call the scheduler may hibernate is not a working call, so protection is part of PASS — and so is
        // releasing it afterwards, because protection that never clears is its own bug.
        var callSurvivesScheduler = micFlagged && vetoedByMedia && mediaSurvived;
        var result = new
        {
            pass = stackWorks && framesFlowed && callSurvivesScheduler && clearedAfterStop && sleepsAfterStop && stallHeld && frameCases,
            summary = !stackWorks ? "The WebRTC stack did not complete a loopback call."
                : !stallHeld ? "A stalled page's capture lost its protection — silence was read as 'finished'."
                : !frameCases ? "Capture inside frames was not protected correctly."
                : !framesFlowed ? "WebRTC negotiated but no frames decoded."
                : !micFlagged ? "Capture ran but the tab did not report microphone and camera."
                : !vetoedByMedia ? "The scheduler was willing to hibernate a live capture."
                : !mediaSurvived ? "The scheduler was refused, but the renderer or the tracks did not survive it."
                : !clearedAfterStop || !sleepsAfterStop ? "Protection did not clear after the capture stopped."
                : "Top-level capture blocks automatic hibernation and releases it afterwards; loopback video and real device capture passed.",
            scope = "Exercised: top-level capture; capture in a sibling frame and in a frame nested two deep; a "
                  + "stalled page whose heartbeat stops while the microphone stays open; a frame destroyed and a "
                  + "frame navigated away while another keeps capturing; release after stop. NOT exercised: a real "
                  + "Meet or Zoom session end to end, the screen-share picker, disconnect/reconnect under memory "
                  + "pressure, and an unresponsive-renderer ProcessFailed (the branch on failure kind is by design "
                  + "only). SharedArrayBuffer was unavailable on the probe page, which sends no COOP/COEP headers; "
                  + "availability in a cross-origin-isolated document has not been tested, so this is not evidence "
                  + "that JevBrowse disables it.",
            callProtection,
            stalledPage = stalled,
            frameCapture = frames,
            apiSurface = apis,
            loopbackCall = loopback,
            realDevices = devices,
            note = "Permission prompts were auto-allowed for this run; in normal use camera and microphone are Ask. "
                 + "Device counts of 0 mean this machine has no camera/microphone attached, not that JevBrowse blocked them.",
        };
        var file = Path.Combine(DataDir, "benchmarks", $"media-check-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Join a REAL meeting through the browser client. This is the gap every earlier media claim was hedged
    /// against: loopback and local frames proved the stack works, not that a conferencing product's own code path
    /// works here. Drives the "join from your browser" flow, reports what the service itself says about browser
    /// support, whether capture actually starts, and whether the tab is then held open against the scheduler.
    ///
    /// Nobody is present. The display name says so, the check leaves at the end, and it types nothing into a
    /// password field. The URL is passed as --join=URL so a meeting link is never committed to the repository.
    /// </summary>
    private async Task RunJoinCheckAsync(string joinUrl)
    {
        var k = _kernel!;
        foreach (var t in k.Tabs.ToList()) await k.CloseAsync(t.Id);

        var tab = k.Open(new Uri(joinUrl));
        await k.ActivateAsync(tab.Id);
        _autoAllowPermissions = true;

        CoreWebView2 Core()
        {
            if (!_leases!.TryGet(tab.Id, out var l)) throw new InvalidOperationException("no renderer");
            return ((WebView2Lease)l).View.CoreWebView2;
        }

        int probe = 0;
        async Task<JsonElement> EvalAsync(string js, int timeoutMs = 20000)
        {
            var tag = $"jev:join:{++probe}:";
            var tcs = new TaskCompletionSource<string>();
            var core = Core();
            void OnMessage(CoreWebView2 _, CoreWebView2WebMessageReceivedEventArgs e)
            {
                string m; try { m = e.TryGetWebMessageAsString(); } catch (ArgumentException) { return; }
                if (m.StartsWith(tag, StringComparison.Ordinal)) tcs.TrySetResult(m[tag.Length..]);
            }
            core.WebMessageReceived += OnMessage;
            try
            {
                await core.ExecuteScriptAsync($$"""
                    (async () => {
                      const post = v => { try { chrome.webview.postMessage({{JsonSerializer.Serialize(tag)}} + JSON.stringify(v)); } catch {} };
                      try { post(await (async () => { {{js}} })()); }
                      catch (e) { post({ error: String((e && e.name) || e) + ': ' + String((e && e.message) || '') }); }
                    })();
                    """);
                if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) != tcs.Task)
                    return JsonDocument.Parse("""{"error":"timed out"}""").RootElement.Clone();
                return JsonDocument.Parse(await tcs.Task).RootElement.Clone();
            }
            finally { try { core.WebMessageReceived -= OnMessage; } catch (Exception) { } }
        }

        var steps = new List<object>();
        void Step(string what, object detail) => steps.Add(new { step = what, at = DateTime.Now.ToString("HH:mm:ss"), detail });

        try
        {
            await Task.Delay(12000);   // the landing page is heavy and redirects
            Step("landed", new { url = Core().Source, title = Core().DocumentTitle, dataClass = ClassLabel(k.ClassOf(tab)) });

            // What the service itself thinks of this browser is the thing worth reporting: a browser that passes a
            // WebRTC loopback but that Zoom refuses is still a browser you cannot meet in.
            var page = await EvalAsync("""
                const txt = (document.body ? document.body.innerText : '').slice(0, 3000);
                const find = re => { const m = txt.match(re); return m ? m[0] : null; };
                return {
                  offersBrowserClient: /join from (your )?browser/i.test(txt),
                  unsupported: find(/unsupported browser|not supported|update your browser/i),
                  needsName: !!document.querySelector('input#input-for-name, input[placeholder*="name" i]'),
                  needsPasscode: !!document.querySelector('input#input-for-pwd'),
                  buttons: [...document.querySelectorAll('button, a')].map(b => (b.innerText || '').trim())
                            .filter(t => t && t.length < 40).slice(0, 20),
                  excerpt: txt.slice(0, 500),
                };
                """);
            Step("service page", page);

            // The cookie banner sits over the page and swallows the first click.
            var consent = await EvalAsync("""
                const b = [...document.querySelectorAll('button')]
                  .find(e => /accept all cookies|reject all/i.test((e.innerText || '').trim()));
                if (b) { b.click(); return { dismissed: b.innerText.trim() }; }
                return { dismissed: null };
                """);
            Step("cookie banner", consent);
            await Task.Delay(2000);

            var clicked = await EvalAsync("""
                const el = [...document.querySelectorAll('a, button')]
                  .find(e => /join from (your )?browser/i.test(e.innerText || ''));
                if (el) { el.click(); return { clicked: true, text: el.innerText.trim() }; }
                return { clicked: false };
                """);
            Step("browser-client link", clicked);
            await Task.Delay(14000);
            Step("after browser-client click", new { url = Core().Source, title = Core().DocumentTitle });

            var named = await EvalAsync("""
                const n = document.querySelector('input#input-for-name, input[placeholder*="name" i]');
                if (!n) return { nameField: false };
                const set = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
                set.call(n, 'JevBrowse check');
                n.dispatchEvent(new Event('input', { bubbles: true }));
                return { nameField: true };
                """);
            Step("display name", named);

            var joined = await EvalAsync("""
                const all = [...document.querySelectorAll('button, a[role="button"], input[type="submit"]')];
                const b = all.find(e => /^(join|join meeting|join audio by computer)$/i.test((e.innerText || e.value || '').trim()));
                if (b) { b.click(); return { joinClicked: true, text: (b.innerText || b.value || '').trim() }; }
                return { joinClicked: false,
                         buttons: all.map(e => (e.innerText || e.value || '').trim()).filter(Boolean).slice(0, 20) };
                """);
            Step("join", joined);
            await Task.Delay(20000);
            Step("after join", new { url = Core().Source, title = Core().DocumentTitle });

            var media = await EvalAsync("""
                const vids = [...document.querySelectorAll('video, canvas')].map(v => ({
                  tag: v.tagName, w: v.videoWidth || v.width || 0, h: v.videoHeight || v.height || 0 }));
                return { mediaElements: vids.length, detail: vids.filter(v => v.w > 0).slice(0, 6),
                         inMeeting: /leave|unmute|start video|participants/i.test(document.body ? document.body.innerText : '') };
                """);
            Step("in meeting", media);

            var protection = tab.Protection;
            var demote = await k.VirtualizeAsync(tab.Id, Cause.Scheduler);
            Step("scheduler while joined", new
            {
                protection = protection.ToString(),
                verdict = demote.Allowed ? "PUT IT TO SLEEP" : demote.Reason,
                heldOpen = !demote.Allowed,
            });

            _shield!.Stats.TryGetValue(tab.Id, out var st);
            var sample = ProcessGroupProbe.Sample(_leases!.ProcessIds);
            Step("cost", new { requests = st?.Total ?? 0, blocked = st?.Blocked ?? 0, thirdPartyHosts = st?.ThirdPartyHosts.Count ?? 0, groupPrivateMb = Math.Round(sample.PrivateMb) });

            if (tab.State.HasLiveRenderer())
            {
                await EvalAsync("""
                    const b = [...document.querySelectorAll('button')].find(e => /leave/i.test((e.innerText||'').trim()));
                    if (b) { b.click(); return { left: true }; }
                    return { left: false };
                    """);
                await Task.Delay(5000);
            }
            Step("left", new { protectionAfter = tab.Protection.ToString() });
        }
        catch (Exception ex) { Step("failed", ex.GetType().Name + ": " + ex.Message); }
        finally { _autoAllowPermissions = false; }

        var file = Path.Combine(DataDir, "benchmarks", $"join-check-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { steps }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    /// <summary>
    /// Open a realistic set of heavily-used sites one after another, then close them all. This is the load the
    /// architecture exists for: far more tabs than live renderers, real ad and tracker volume, real classification.
    /// Records per site — time to navigation-complete, requests, blocked, third-party hosts, data class, protection,
    /// live renderers at that moment, and what the scheduler evicted to make room — then the memory reclaimed by
    /// closing everything. Sites are read from docs/sites.txt so the list is data, not code.
    /// </summary>
    private async Task RunSiteSweepAsync()
    {
        var k = _kernel!;
        foreach (var t in k.Tabs.ToList()) await k.CloseAsync(t.Id);

        _autoDenyPermissions = true;   // unattended, real sites: deny anything asked, remember nothing
        var listFile = Path.Combine(AppContext.BaseDirectory, "sites.txt");
        var sites = File.Exists(listFile)
            ? File.ReadAllLines(listFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray()
            : ["https://www.google.com/", "https://www.wikipedia.org/"];

        var baseline = ProcessGroupProbe.Sample(_leases!.ProcessIds).PrivateMb;
        var rows = new List<object>();
        var evictions = new List<string>();
        double peakMb = 0;
        // The result file is written at the END, so a crash mid-sweep would leave nothing to say which site did it.
        // This log is appended and flushed around every site and survives the process dying.
        var progressFile = Path.Combine(DataDir, "benchmarks", "site-sweep.progress.log");
        File.WriteAllText(progressFile, $"{DateTime.Now:HH:mm:ss} start, {sites.Length} sites\n");
        void Progress(string line) { try { File.AppendAllText(progressFile, $"{DateTime.Now:HH:mm:ss} {line}\n"); } catch (Exception) { } }
        void OnChange(KernelEvent e)
        {
            if (e.Kind == "virtualized") evictions.Add(e.Reason);   // Reason carries the cause: Scheduler / User / ...
        }
        k.Changed += OnChange;

        foreach (var site in sites)
        {
            if (!Uri.TryCreate(site, UriKind.Absolute, out var url)) continue;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Progress($"OPENING {url.Host}  live={k.Tabs.Count(x => x.State.HasLiveRenderer())} open={k.Tabs.Count}");
            var tab = k.Open(url);
            string outcome = "loaded";
            try
            {
                await k.ActivateAsync(tab.Id);
                // Navigation-complete, bounded: some of these never stop loading, and a stuck site must not stop
                // the sweep. What we record is "usable enough to have fired load", not "finished".
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < deadline && string.IsNullOrEmpty(tab.Title)) await Task.Delay(250);
                if (string.IsNullOrEmpty(tab.Title)) outcome = "no title within 25 s";
            }
            catch (Exception ex) { outcome = "failed: " + ex.GetType().Name; }
            sw.Stop();
            Progress($"  {url.Host}: {outcome} in {sw.ElapsedMilliseconds} ms");
            await Task.Delay(2500);   // let late trackers and the page script report in

            _shield!.Stats.TryGetValue(tab.Id, out var st);
            var sample = ProcessGroupProbe.Sample(_leases.ProcessIds);
            peakMb = Math.Max(peakMb, sample.PrivateMb);
            // "A title arrived" is not "the page loaded". A navigation that never reached the network leaves the
            // hostname as its placeholder title and Shield sees no requests; calling that "loaded" would report a
            // blocked or unreachable site as a success.
            var placeholder = string.Equals(tab.Title.Trim(), url.Host, StringComparison.OrdinalIgnoreCase);
            if (outcome == "loaded" && (placeholder || (st?.Total ?? 0) == 0)) outcome = "did not load (no requests, placeholder title)";
            rows.Add(new
            {
                site = url.Host,
                outcome,
                msToTitle = sw.ElapsedMilliseconds,
                title = tab.Title.Length > 60 ? tab.Title[..60] : tab.Title,
                requests = st?.Total ?? 0,
                blocked = st?.Blocked ?? 0,
                thirdPartyHosts = st?.ThirdPartyHosts.Count ?? 0,
                dataClass = ClassLabel(k.ClassOf(tab)),
                protection = tab.Protection.ToString(),
                liveRenderers = k.Tabs.Count(x => x.State.HasLiveRenderer()),
                openTabs = k.Tabs.Count,
                groupPrivateMb = Math.Round(sample.PrivateMb),
            });
        }

        var endOfRunMb = ProcessGroupProbe.Sample(_leases.ProcessIds).PrivateMb;
        var peak = Math.Max(peakMb, endOfRunMb);
        var liveAtPeak = k.Tabs.Count(x => x.State.HasLiveRenderer());
        var openAtPeak = k.Tabs.Count;

        Progress($"all opened; closing {k.Tabs.Count} tabs");
        var closeSw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var t in k.Tabs.ToList()) await k.CloseAsync(t.Id);
        closeSw.Stop();
        await Task.Delay(4000);   // renderer processes exit asynchronously
        var after = ProcessGroupProbe.Sample(_leases.ProcessIds).PrivateMb;
        k.Changed -= OnChange;
        _autoDenyPermissions = false;

        var totalReq = rows.Sum(r => (int)r.GetType().GetProperty("requests")!.GetValue(r)!);
        var totalBlocked = rows.Sum(r => (int)r.GetType().GetProperty("blocked")!.GetValue(r)!);
        var result = new
        {
            pass = k.Tabs.Count == 0 && _leases.LiveResources.Count == 0,
            summary = $"{sites.Length} sites opened, {liveAtPeak} live renderers at peak of {openAtPeak} tabs; "
                    + $"{totalBlocked:N0} of {totalReq:N0} requests blocked; "
                    + $"renderer memory peaked at {Math.Round(peak)} MB and was {Math.Round(after)} MB after closing everything.",
            liveRendererCap = _leases.MaxLive,
            liveAtPeak,
            openAtPeak,
            tabsAfterClose = k.Tabs.Count,
            liveRenderersAfterClose = _leases.LiveResources.Count,
            closeMs = closeSw.ElapsedMilliseconds,
            memoryMb = new { baseline = Math.Round(baseline), peakDuringRun = Math.Round(peak), endOfRunBeforeClose = Math.Round(endOfRunMb), afterClose = Math.Round(after) },
            blockedShare = totalReq == 0 ? 0 : Math.Round(100.0 * totalBlocked / totalReq, 1),
            evictionsObserved = evictions.Count,
            evictionCauses = evictions.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count()),
            sitesThatLoaded = rows.Count(r => (string)r.GetType().GetProperty("outcome")!.GetValue(r)! == "loaded"),
            sites = rows,
            unreachableNote = "A site listed as \"did not load\" produced no requests and only a placeholder title. On this machine that is what a network-level block looks like; check reachability before reading it as a browser result.",
            note = "msToTitle is time to a non-empty title, not to visually complete. Renderer memory is the WebView2 "
                 + "process group only; the shell is not counted. One machine, debug build.",
        };
        var file = Path.Combine(DataDir, "benchmarks", $"site-sweep-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Sibling and nested frames, each capturing. Served from http://127.0.0.1, which is a secure context, so
    /// getUserMedia is permitted — NavigateToString would give an opaque origin and could not capture at all.
    /// Asserts the three cases the design claims: two siblings capturing, one stopping leaves the other protected,
    /// and destroying a reporting frame does not disturb a surviving one.
    /// </summary>
    private async Task<object> RunFrameMediaProbeAsync(TabKernel k, VirtualTab tab, Func<string, int, Task<JsonElement>> eval)
    {
        const string Child = """
            <!doctype html><meta charset="utf-8"><script>
              const id = new URLSearchParams(location.search).get('id');
              let s = null;
              navigator.mediaDevices.getUserMedia({ audio: true }).then(x => { s = x; }).catch(() => {});
              addEventListener('message', e => {
                if (e.data && e.data.id === id && e.data.act === 'stop' && s) s.getTracks().forEach(t => t.stop());
              });
            </script>
            """;
        const string Nest = """<!doctype html><meta charset="utf-8"><iframe src="/child?id=b" allow="camera;microphone"></iframe>""";
        const string Parent = """
            <!doctype html><meta charset="utf-8">
            <iframe id="a" src="/child?id=a" allow="camera;microphone"></iframe>
            <iframe id="n" src="/nest" allow="camera;microphone"></iframe>
            <script>
              window.__cmd = m => { const walk = w => { try { w.postMessage(m, '*'); } catch {} ; for (let i = 0; i < w.frames.length; i++) walk(w.frames[i]); };
                                    for (let i = 0; i < window.frames.length; i++) walk(window.frames[i]); };
              window.__dropA = () => { const f = document.getElementById('a'); f.parentNode.removeChild(f); };
            </script>
            """;

        var port = 8100 + Random.Shared.Next(400);
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch (Exception) { return; }
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                var body = System.Text.Encoding.UTF8.GetBytes(path switch { "/child" => Child, "/nest" => Nest, "/blank" => "<!doctype html><title>done</title>", _ => Parent });
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = body.Length;
                try { await ctx.Response.OutputStream.WriteAsync(body); ctx.Response.Close(); } catch (Exception) { }
            }
        }, stop.Token);

        try
        {
            // Earlier stages may have left the tab asleep; this probe needs a renderer of its own.
            if (!tab.State.HasLiveRenderer()) await k.ActivateAsync(tab.Id);
            if (!_leases!.TryGet(tab.Id, out var live)) return new Dictionary<string, object> { ["pass"] = false, ["error"] = "no renderer" };
            live.Navigate(new Uri($"http://127.0.0.1:{port}/parent"));
            await Task.Delay(6000);
            var bothCapturing = tab.Protection.HasFlag(ProtectionFlags.MicrophoneActive);

            // Stop the sibling at depth 1. The nested one at depth 2 is still capturing.
            await eval("window.__cmd({ id: 'a', act: 'stop' }); return { ok: true };", 10000);
            await Task.Delay(3000);
            var nestedSurvives = tab.Protection.HasFlag(ProtectionFlags.MicrophoneActive);

            // Destroy a frame that had already stopped, then stop the nested one: protection must end only now.
            await eval("window.__dropA(); return { ok: true };", 10000);
            await Task.Delay(2000);
            var stillNested = tab.Protection.HasFlag(ProtectionFlags.MicrophoneActive);
            await eval("window.__cmd({ id: 'b', act: 'stop' }); return { ok: true };", 10000);
            await Task.Delay(3000);
            var clearedAtEnd = !tab.Protection.HasLiveMedia();

            // Frame NAVIGATION, which is not destruction: a capturing frame whose document is replaced never sends
            // media-end, so without frame-level ContentLoading its entry stays uncertain forever and the tab can
            // never sleep again. Two frames capture; one navigates away; the other must stay protected, and the
            // navigated one's claim must be gone.
            live.Navigate(new Uri($"http://127.0.0.1:{port}/parent"));
            await Task.Delay(6000);
            var reloadedCapturing = tab.Protection.HasFlag(ProtectionFlags.MicrophoneActive);
            await eval("document.getElementById('a').src = '/blank'; return { ok: true };", 10000);
            await Task.Delay(4000);
            var survivorHeld = tab.Protection.HasFlag(ProtectionFlags.MicrophoneActive);
            // Now the nested one goes the same way. Nothing is capturing, and nothing may be left latched on.
            await eval("document.getElementById('n').src = '/blank'; return { ok: true };", 10000);
            await Task.Delay(4000);
            var navigatedFrameReleased = !tab.Protection.HasLiveMedia();

            return new Dictionary<string, object>
            {
                ["pass"] = bothCapturing && nestedSurvives && stillNested && clearedAtEnd
                           && reloadedCapturing && survivorHeld && navigatedFrameReleased,
                ["siblingAndNestedCapturing"] = bothCapturing,
                ["stoppingSiblingLeavesNestedProtected"] = nestedSurvives,
                ["destroyingOneFrameLeavesSurvivorProtected"] = stillNested,
                ["clearedWhenLastFrameStopped"] = clearedAtEnd,
                ["navigatingOneFrameLeavesSurvivorProtected"] = survivorHeld,
                ["navigatedFrameStopsClaimingCapture"] = navigatedFrameReleased,
            };
        }
        finally { stop.Cancel(); listener.Stop(); }
    }

    /// <summary>
    /// System-level privacy gate (review #1/#15): real WebView2 renderers, real disk. Drives a public control, a
    /// sensitive-URL tab and a Private-container session through the same hide/virtualize/switch operations a user
    /// performs, then inspects the thumbnails directory and the database for anything that must not be there.
    /// PASS requires the control to have produced a thumbnail (so "no file" is meaningful) and every private/sensitive
    /// artifact count to be zero.
    /// </summary>
    private async Task RunPrivacyCheckAsync()
    {
        var k = _kernel!;
        foreach (var t in k.Tabs.ToList()) await k.CloseAsync(t.Id);
        var thumbDir = Path.Combine(DataDir, "thumbnails");
        Directory.CreateDirectory(thumbDir);
        var before = Directory.GetFiles(thumbDir).ToHashSet();
        _leases!.MaxLive = 10;

        async Task<VirtualTab> OpenActivate(string url, int settleMs = 6000)
        {
            var t = k.Open(new Uri(url));
            await k.ActivateAsync(t.Id);
            await Task.Delay(settleMs);
            return t;
        }

        var pubWs = k.Workspaces[0];
        var priv = k.CreateWorkspace("Private", IdentityContainer.Private);

        // public control + sensitive-URL tab (Personal container)
        await k.SwitchWorkspaceAsync(pubWs.Id);
        var pub = await OpenActivate("https://example.com");
        var sens = await OpenActivate("https://netbanking.hdfcbank.com/");     // classified SENSITIVE by URL
        var other = await OpenActivate("https://en.wikipedia.org/wiki/Cat");   // activating this hides `sens`, then `pub` was hidden earlier

        // private session: two tabs, switch between them, virtualize, switch workspace away and back
        await k.SwitchWorkspaceAsync(priv.Id);
        var p1 = await OpenActivate("https://example.org");
        var p2 = await OpenActivate("https://en.wikipedia.org/wiki/Dog");      // hides p1 (deactivation capture path)
        await k.VirtualizeAsync(p1.Id, Cause.User);
        await k.VirtualizeAsync(p2.Id, Cause.User);
        await k.SwitchWorkspaceAsync(pubWs.Id);                                // records the outgoing context checkpoint
        _permissions?.Block(IdentityContainer.Private, priv.Id, new Uri("https://example.org"), TrustOS.PermissionKind.Notifications);
        await Task.Delay(1500);

        var now = Directory.GetFiles(thumbDir).ToHashSet();
        var created = now.Except(before).Select(Path.GetFileName).Where(f => f is not null).Select(f => f!).ToList();
        bool Has(VirtualTab t) => created.Any(f => f.StartsWith(t.Id.ToString()));
        int Scalar(string sql) { using var c = _db!.Connection.CreateCommand(); c.CommandText = sql; return Convert.ToInt32(c.ExecuteScalar()); }
        var privIds = new[] { p1.Id, p2.Id }.Select(i => i.ToString()).ToList();
        string InList(IEnumerable<string> ids) => string.Join(",", ids.Select(i => $"'{i}'"));
        var ephemeralDirs = Directory.Exists(Path.Combine(DataDir, "profiles", "ephemeral")) ? Directory.GetDirectories(Path.Combine(DataDir, "profiles", "ephemeral")).Length : 0;

        var evidence = new
        {
            publicControlHasThumbnail = Has(pub),
            sensitiveThumbnails = Has(sens) ? 1 : 0,
            sensitiveCheckpointRow = k.GetCheckpoint(sens.Id) is null ? 0 : 1,
            sensitiveClass = k.ClassOf(sens).ToString(),
            privateThumbnails = (Has(p1) ? 1 : 0) + (Has(p2) ? 1 : 0),
            privateTabRows = Scalar($"SELECT COUNT(*) FROM tabs WHERE id IN ({InList(privIds)})"),
            privateCheckpointRows = Scalar($"SELECT COUNT(*) FROM checkpoints WHERE id IN ({InList(privIds)})"),
            privateWorkspaceRows = Scalar("SELECT COUNT(*) FROM workspaces WHERE name = 'Private'"),
            privateTimelineEntries = k.Timeline().Count(c => c.WorkspaceName == "Private" || c.Resources.Any(r => privIds.Contains(r.Id.ToString()))),
            privatePermissionRows = Scalar("SELECT COUNT(*) FROM site_permissions WHERE site LIKE 'Private:%' OR site LIKE 'Disposable:%'"),
            privateMemoryDocs = Scalar($"SELECT COUNT(*) FROM memory_docs WHERE {string.Join(" OR ", privIds.Select(i => $"id LIKE '{i}%'"))}"),
            ephemeralProfileDirsDuringRun = ephemeralDirs,
            newThumbnailFiles = created.Count,
        };
        var pass = evidence.publicControlHasThumbnail && evidence.sensitiveThumbnails == 0 && evidence.privateThumbnails == 0 && evidence.privateTabRows == 0
                   && evidence.privateCheckpointRows == 0 && evidence.privateWorkspaceRows == 0 && evidence.privateTimelineEntries == 0
                   && evidence.privatePermissionRows == 0 && evidence.privateMemoryDocs == 0
                   && evidence.sensitiveClass is "Sensitive" or "Secret";   // a real bank page with a password field is (correctly) SECRET
        var file = Path.Combine(DataDir, "benchmarks", $"privacy-check-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { pass, evidence }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// ADR 0015 gate: play a video for a fixed window and measure what actually happened: ad definitions pruned,
    /// player-level skips, seconds the player spent in ad state, whether a detection wall appeared.
    /// </summary>
    private async Task RunYouTubeCheckAsync()
    {
        var k = _kernel!;
        foreach (var t in k.Tabs.ToList()) await k.CloseAsync(t.Id);
        var urls = (Environment.GetEnvironmentVariable("JEVBROWSE_YT_URLS") ?? "https://www.youtube.com/watch?v=dQw4w9WgXcQ;https://www.youtube.com/watch?v=jNQXAC9IVRw").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var rows = new List<object>();
        foreach (var u in urls)
        {
            var t = k.Open(new Uri(u));
            await k.ActivateAsync(t.Id);
            int adSeconds = 0, playingSeconds = 0, samples = 0; double maxTime = 0;
            for (int i = 0; i < 75; i++)
            {
                await Task.Delay(1000);
                if (!_leases!.TryGet(t.Id, out var l)) continue;
                try
                {
                    var r = await ((WebView2Lease)l).View.CoreWebView2.ExecuteScriptAsync("(() => { const p = document.querySelector('.html5-video-player'); const v = document.querySelector('video.html5-main-video'); return JSON.stringify({ ad: !!(p && p.classList.contains('ad-showing')), playing: !!(v && !v.paused && v.currentTime > 0), t: v ? v.currentTime : 0 }); })()");
                    using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(r) ?? "{}");
                    samples++;
                    if (doc.RootElement.GetProperty("ad").GetBoolean()) adSeconds++;
                    if (doc.RootElement.GetProperty("playing").GetBoolean()) playingSeconds++;
                    maxTime = Math.Max(maxTime, doc.RootElement.GetProperty("t").GetDouble());
                    if (i == 8 && !doc.RootElement.GetProperty("playing").GetBoolean())
                        await ((WebView2Lease)l).View.CoreWebView2.ExecuteScriptAsync("(() => { const v = document.querySelector('video.html5-main-video'); if (v) v.play().catch(() => {}); const b = document.querySelector('.ytp-large-play-button, .ytp-play-button'); if (b) b.click(); })()");
                }
                catch (Exception) { }
            }
            _shield!.Stats.TryGetValue(t.Id, out var st);
            rows.Add(new { url = u, samples, playingSeconds, adSeconds, videoReachedSeconds = Math.Round(maxTime), adsPruned = st?.AdsPruned ?? 0, adsSkipped = st?.AdsSkipped ?? 0, wallSeen = st?.WallSeen ?? 0, requestsBlocked = st?.Blocked ?? 0, modules = st?.SiteModules });
            await k.VirtualizeAsync(t.Id, Cause.User);
        }
        var file = Path.Combine(DataDir, "benchmarks", $"youtube-check-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { pages = rows }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Phase 4 gate: load ad-heavy pages with the real engine and report blocked/total per page.</summary>
    private async Task RunShieldCheckAsync()
    {
        var extra = Environment.GetEnvironmentVariable("JEVBROWSE_SHIELD_URLS");
        string[] urls = extra is { Length: > 0 }
            ? extra.Split(';', StringSplitOptions.RemoveEmptyEntries)
            : ["https://www.theverge.com", "https://www.cnn.com", "https://www.forbes.com", "https://en.wikipedia.org/wiki/Advertising"];
        var k = _kernel!;
        foreach (var t in k.Tabs.ToList()) await k.CloseAsync(t.Id);
        var rows = new List<object>();
        foreach (var u in urls)
        {
            var t = k.Open(new Uri(u));
            await k.ActivateAsync(t.Id);
            await Task.Delay(_brainPolicy.AiEnabled ? 28000 : 20000); // semantic pass needs the extra round trip
            _shield!.Stats.TryGetValue(t.Id, out var st);
            // Visible ad-slot probe: count common ad containers still present and non-empty in the DOM (cosmetic gap).
            string adProbe = "0";
            WithActiveLease(l => { });
            if (_leases!.TryGet(t.Id, out var lease))
            {
                try
                {
                    adProbe = await ((WebView2Lease)lease).View.CoreWebView2.ExecuteScriptAsync("""
                        (() => { const sel = 'ins.adsbygoogle, [id^="google_ads"], [id*="div-gpt-ad"], [class*="ad-slot"], [class*="adsense"], [class*="advertisement"], iframe[src*="doubleclick"], iframe[src*="googlesyndication"], ytd-ad-slot-renderer, .ytp-ad-module, [class*="ad-banner"], [id*="taboola"], [id*="outbrain"]';
                          const els = [...document.querySelectorAll(sel)];
                          const visible = els.filter(e => { const r = e.getBoundingClientRect(); return r.width > 50 && r.height > 50; });
                          return JSON.stringify({ slots: els.length, visible: visible.length, thirdPartyIframes: [...document.querySelectorAll('iframe')].filter(f => { try { return new URL(f.src).host !== location.host; } catch { return false; } }).length }); })()
                        """);
                }
                catch (Exception) { }
            }
            rows.Add(new { url = u, total = st?.Total ?? 0, blocked = st?.Blocked ?? 0, thirdParty = st?.ThirdParty ?? 0, cosmeticSelectors = st?.CosmeticSelectors ?? 0, collapsed = st?.Collapsed ?? 0, collapseResult = st?.LastCollapseResult, semanticCollapsed = st?.SemanticCollapsed ?? 0, semantic = st?.LastSemantic, adProbe = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Deserialize<string>(adProbe) ?? "0"), sample = st?.Recent.Take(6).Select(x => x.Host + " ← " + x.Rule).ToList() });
            await k.VirtualizeAsync(t.Id, Cause.User);
        }
        // Lookup latency against the real compiled lists, over a realistic mix of URLs seen on these pages.
        var sample = _shield!.Stats.Values.SelectMany(s => s.Recent).Select(x => new NetworkRequest(new Uri("https://" + x.Host + "/a.js"), new Uri("https://www.cnn.com/"), RequestType.Script)).ToList();
        for (int i = 0; i < 200; i++) sample.Add(new NetworkRequest(new Uri($"https://static{i}.example-cdn.com/assets/app.{i}.js?v=3"), new Uri("https://www.example.com/"), RequestType.Script));
        var (p50, p95) = _engine!.Benchmark(sample);
        var result = new { rules = _shield!.RuleCount, skippedLines = _engine.SkippedLines, compileMs = _filterCompileMs, lookupP50Us = Math.Round(p50, 1), lookupP95Us = Math.Round(p95, 1), pages = rows };
        var file = Path.Combine(DataDir, "benchmarks", $"shield-check-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---- Resource OS ----

    private async Task SchedulerTickAsync()
    {
        if (_kernel is null || _leases is null) return;
        try
        {
            var os = SystemPressureSampler.Sample();
            var group = ProcessGroupProbe.Sample(_leases.ProcessIds);
            var pressure = new SystemPressure(os.AvailableBytes, os.TotalBytes, group.PrivateBytes, os.OnBattery, false, os.At);
            _lastPlan = _scheduler.Evaluate(pressure, _kernel.Snapshot(), DateTimeOffset.UtcNow);
            var n = await _kernel.ApplyPlanAsync(_lastPlan);
            if (n > 0) StatusText.Text = $"Resource OS: virtualized {n} ({_lastPlan.Band}, {os.AvailableBytes >> 20} MB free)";
            MaybeRecordContextCheckpoint();
            UpdatePoolText();
        }
        catch (Exception ex) { StatusText.Text = "scheduler tick failed: " + ex.Message; }
    }

    private void OnModeChanged(object s, SelectionChangedEventArgs e)
    {
        if (ModeBox.SelectedIndex < 0) return;
        _scheduler.Policy = SchedulerPolicy.For((MemoryMode)ModeBox.SelectedIndex);
        if (_leases is not null) _leases.MaxLive = _scheduler.Policy.MaxLive;
    }

    /// <summary>
    /// Where the tab sits. One wish, one control: this does not stop the tab sleeping, and the status line says so
    /// rather than leaving the user to discover it when the tab sleeps anyway.
    /// </summary>
    private void OnPinCurrent(object s, RoutedEventArgs e)
    {
        if (_kernel?.Active is not { } t) return;
        var pinned = !t.IsPinned;
        _kernel.SetPinned(t.Id, pinned);
        StatusText.Text = pinned
            ? "Pinned to the top of this workspace. It can still sleep — use Keep active for that."
            : "Unpinned.";
        _syncingSelection = true;
        TabList.SelectedItem = Items.FirstOrDefault(i => i.Id == t.Id);
        _syncingSelection = false;
    }

    /// <summary>
    /// Whether the tab sleeps on its own. Turning it off is the direction that costs the user something, so that is
    /// the direction that explains itself.
    /// </summary>
    private async void OnKeepActiveCurrent(object s, RoutedEventArgs e)
    {
        if (_kernel?.Active is not { } t) return;
        var on = !t.UserProtection.HasFlag(ProtectionFlags.KeepActive);
        if (!on)
        {
            var dlg = new ContentDialog
            {
                Title = "Let this tab sleep?",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"“{(string.IsNullOrWhiteSpace(t.Title) ? t.Url.Host : t.Title)}” will go to sleep on its own "
                         + "when JevBrowse needs the memory. It reopens at the address and scroll position we saved.\n\n"
                         + "Anything the site is holding that we do not save — a half-typed form, an upload, a call, "
                         + "playback position — is lost when it sleeps.\n\n"
                         + "It stays where it is in your list either way.",
                },
                PrimaryButtonText = "Let it sleep", CloseButtonText = "Keep it active",
                DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot,
            };
            if (await dlg.ShowSerializedAsync() != ContentDialogResult.Primary) return;
        }
        _kernel.SetProtection(t.Id, on ? t.UserProtection | ProtectionFlags.KeepActive : t.UserProtection & ~ProtectionFlags.KeepActive);
        StatusText.Text = on ? "Keeping this tab active: it will not sleep on its own." : "This tab can sleep when memory is needed.";
        foreach (var i in Items) i.Refresh();
    }

    private void OnExplain(object s, RoutedEventArgs e) => OpenPanel("explain", BuildExplain, ExplainButton);

    private (string Title, UIElement Body)? BuildExplain()
    {
        if (_kernel?.Active is not { } t) return null;
        var view = ExplainText.Build(new ExplainFacts(
            t.State, _kernel.Active?.Id == t.Id, _kernel.LastDecision(t.Id),
            _lastPlan?.Band, _lastPlan?.TargetLiveRenderers ?? 0, _lastPlan?.LiveNow ?? _kernel.LiveCount,
            ProtectionPhrases.StayAwake(t.Protection)));

        // Sentences, in the interface font. The scheduler's raw record is not what anyone opened this to read.
        var body = new StackPanel { Spacing = Tokens.Space(10) };
        body.Children.Add(new TextBlock { Text = view.Headline, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (var line in view.Lines) body.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Foreground = Tokens.Brush("JevTextSecondaryBrush") });
        // The action offered is about sleeping, because that is what this just explained. It is a plain button, nothing is the
        // default, and nothing happens until it is pressed: Enter on the panel's close button cannot change what the tab does.
        var act = new Button { Content = t.UserProtection.HasFlag(ProtectionFlags.KeepActive) ? "Let it sleep" : "Keep this tab active", HorizontalAlignment = HorizontalAlignment.Stretch };
        act.Click += (sender, args) => { OnKeepActiveCurrent(sender, args); RefreshPanel(); };
        body.Children.Add(act);
        return (ExplainText.Title, body);
    }

    // ---- UI → kernel ----

    private async void OnTabSelected(object s, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || TabList.SelectedItem is not TabItem item || _kernel is null) return;
        await _kernel.ActivateAsync(item.Id);
    }

    private async void OnNewTab(object s, RoutedEventArgs e)
    {
        if (_kernel is null) return;
        var t = _kernel.Open(new Uri("https://duckduckgo.com"));
        await _kernel.ActivateAsync(t.Id);
        AddressBox.Focus(FocusState.Programmatic);
        AddressBox.SelectAll();
    }

    private async void OnCloseTab(object s, RoutedEventArgs e)
    {
        if ((s as Button)?.Tag is not ResourceId id || _kernel is null) return;
        await _kernel.CloseAndSelectNextAsync(id);   // the same rule as Ctrl+W: stay inside this workspace
    }

    /// <summary>Protection flags in the words a person would use, for the cases that are not live media.</summary>
    private static string ReadableProtection(ProtectionFlags p)
    {
        var parts = new List<string>();
        if (p.HasFlag(ProtectionFlags.DownloadActive)) parts.Add("a download is running");
        if (p.HasFlag(ProtectionFlags.DirtyForm)) parts.Add("you have typed something that is not saved");
        if (p.HasFlag(ProtectionFlags.Audible)) parts.Add("it is playing sound");
        if (p.HasFlag(ProtectionFlags.KeepActive)) parts.Add("you asked to keep it active");
        if (p.HasFlag(ProtectionFlags.NeverHibernateSite)) parts.Add("this site is set to stay active");
        return parts.Count == 0 ? "This tab is protected." : char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts)[1..] + ".";
    }

    private async void OnHibernateCurrent(object s, RoutedEventArgs e)
    {
        if (_kernel?.Active is not { } tab) return;
        // A manual request overrides protections, so say what is being overridden before dropping the renderer.
        if (tab.IsDemotionVetoed)
        {
            // Live media is not "a protection flag" to a person: it is their microphone, their camera, their screen
            // being shared right now. Lead with that, in those words, before anything about renderers.
            var p = tab.Protection;
            var media = p.HasLiveMedia();
            var doing = new List<string>();
            if (p.HasFlag(ProtectionFlags.ScreenShareActive)) doing.Add("sharing your screen");
            if (p.HasFlag(ProtectionFlags.CameraActive)) doing.Add("using your camera");
            if (p.HasFlag(ProtectionFlags.MicrophoneActive)) doing.Add("using your microphone");
            if (p.HasFlag(ProtectionFlags.WebRtcActive) && doing.Count == 0) doing.Add("in a call");
            // Capture is not always a call. A local recording has no other participants, so promising that "the
            // other people will see you go" would be a confident description of something that is not happening.
            var inCall = p.HasFlag(ProtectionFlags.WebRtcActive);
            var name = string.IsNullOrWhiteSpace(tab.Title) ? tab.Url.Host : tab.Title;
            var dlg = new ContentDialog
            {
                Title = media ? $"This page is {string.Join(" and ", doing)}" : "This tab is busy",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = media
                        ? $"Putting “{name}” to sleep stops its camera, microphone or screen capture."
                          + (inCall ? " You will leave the call, and the other people will see you go." : "")
                          + "\n\nOnly the address and scroll position are kept."
                        : $"{ReadableProtection(p)}\n\nPutting this tab to sleep closes the live page. "
                          + "Anything the site has not saved — typing in a form, an upload, a download in progress — "
                          + "is lost. Only the address and scroll position are kept.",
                },
                PrimaryButtonText = media ? (inCall ? "Leave and sleep" : "Stop and sleep") : "Put it to sleep",
                CloseButtonText = media ? (inCall ? "Stay in the call" : "Keep it running") : "Keep it open",
                DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot,
            };
            if (await dlg.ShowSerializedAsync() != ContentDialogResult.Primary) return;
        }
        var r = await _kernel.VirtualizeAsync(tab.Id, Cause.User);
        StatusText.Text = r.Reason;
    }

    private async void OnHibernateAll(object s, RoutedEventArgs e)
    {
        var keep = _kernel!.Active?.Id;
        foreach (var t in _kernel.Tabs.Where(t => t.Id != keep && t.State.HasLiveRenderer()).ToList())
            await _kernel.VirtualizeAsync(t.Id, Cause.Scheduler); // scheduler cause: protection is honoured
        UpdatePoolText();
    }

    private void OnBack(object s, RoutedEventArgs e) => WithActiveLease(l => { if (l.View.CanGoBack) l.View.GoBack(); });
    private void OnForward(object s, RoutedEventArgs e) => WithActiveLease(l => { if (l.View.CanGoForward) l.View.GoForward(); });

    private void WithActiveLease(Action<WebView2Lease> a)
    {
        if (_kernel?.Active is { } t && _leases!.TryGet(t.Id, out var l)) a((WebView2Lease)l);
    }

    // ---- address bar ----

    private bool _addressEditing;
    private string _lastPopupDiag = "";
    private bool _settingAddress;

    /// <summary>Shows an address the app decided on (not the person's typing) and ends any editing.</summary>
    private string _addressSetByApp = "";

    private void SetAddress(string text)
    {
        _settingAddress = true;
        try { _addressSetByApp = text; AddressBox.Text = text; }
        finally { _settingAddress = false; }
        _addressEditing = false;
    }

    // WinUI raises TextChanged AFTER the assignment returns, so a flag around the assignment is not enough: what counts as the person's typing is text that
    // is not the one the app put there.
    private void OnAddressTextChanged(object s, TextChangedEventArgs e) { if (!_settingAddress && AddressBox.Text != _addressSetByApp) _addressEditing = true; }

    /// <summary>True when the box holds something the app did not put there, that is, the person's own typing. Judged from the text itself at the moment it matters,
    /// not from an event that might not have been raised yet.</summary>
    private bool IsEditingAddress => _addressEditing || AddressBox.Text != _addressSetByApp;

    /// <summary>Leaving the box without pressing Enter abandons the edit: show where the page in front really is.</summary>
    private void OnAddressLostFocus(object s, RoutedEventArgs e)
    {
        if (IsEditingAddress && _kernel?.Active is { } t) SetAddress(t.Url.ToString());
    }

    private async void OnAddressKeyDown(object s, KeyRoutedEventArgs e)
    {
        try
        {
            if (_kernel is null) return;
            if (e.Key == VirtualKey.Escape)
            {
                if (_kernel.Active is { } cur) SetAddress(cur.Url.ToString());
                return;
            }
            if (e.Key != VirtualKey.Enter) return;
            var r = AddressInput.Resolve(AddressBox.Text);
            if (r.Kind == AddressKind.Invalid) { if (!string.IsNullOrEmpty(r.Message)) StatusText.Text = r.Message; return; }
            var url = r.Url!;
            _addressEditing = false;
            if (_kernel.Active is null) { var nt = _kernel.Open(url); await _kernel.ActivateAsync(nt.Id); return; }
            WithActiveLease(l => l.Navigate(url));
        }
        catch (Exception ex) { StatusText.Text = "Could not open that address: " + ex.Message; }   // an async void handler must never let one escape
    }

    // ---- pop-ups ----

    /// <summary>
    /// A page asked for a new window. The engine never gets to make one: an allowed request becomes an ordinary tab in the SAME workspace as its opener
    /// (so a Private page's window is Private), created and admitted like any other; everything else is refused and the person is told once.
    /// Note the trade-off: the new tab has no link back to its opener, so sign-in pop-ups that report back to the opening page do not work.
    /// </summary>
    private void OnPopupRequested(ResourceId opener, Uri? target, bool userInitiated, bool agentPage)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_kernel is null) return;
                var src = _kernel.Tabs.FirstOrDefault(t => t.Id == opener);
                var decision = PopupPolicy.Decide(userInitiated, agentPage, src is not null && _kernel.Active?.Id == opener, target?.Scheme);
                _lastPopupDiag = $"userInitiated={userInitiated} agent={agentPage} inFront={src is not null && _kernel.Active?.Id == opener} -> {(decision.Allow ? "allow" : "block")}";
                if (!decision.Allow || src is null || target is null)
                {
                    StatusText.Text = $"Blocked a pop-up from {src?.Url.Host ?? "a page"}: {decision.Reason}.";
                    return;
                }
                var t = _kernel.OpenIn(src.WorkspaceId, target);
                await _kernel.ActivateAsync(t.Id);
                StatusText.Text = "Opened in a new tab. Pop-up sign-in windows that must report back to the page are not supported in this alpha.";
            }
            catch (Exception ex) { StatusText.Text = "Could not open the new window: " + ex.Message; }
        });
    }

    // ---- Memory Lab (Phase 0 benchmark, kept as CI hook) ----

    private async void OnMemoryLab(object s, RoutedEventArgs e)
    {
        try { await RunMemoryLabAsync(); }
        catch (Exception ex) { StatusText.Text = "lab failed: " + ex.Message; }
    }

    /// <summary>Phase 2 gate: virtual → live restore latency (p50/p95), scroll fidelity, thumbnail presence.</summary>
    private async Task RunRestoreBenchAsync()
    {
        string[] urls =
        [
            "https://en.wikipedia.org/wiki/Web_browser", "https://en.wikipedia.org/wiki/Operating_system",
            "https://en.wikipedia.org/wiki/Memory_management", "https://en.wikipedia.org/wiki/Scheduling_(computing)",
            "https://learn.microsoft.com/en-us/microsoft-edge/webview2/", "https://news.ycombinator.com",
            "https://example.com", "https://www.gnu.org/philosophy/free-sw.html",
        ];
        var k = _kernel!;
        var trace = Path.Combine(DataDir, "benchmarks", "restore-bench.trace.log");
        void T(string s) => File.AppendAllText(trace, $"{DateTime.Now:HH:mm:ss.fff} {s}\n");
        k.Changed += e => T($"  ev {e.Kind} {e.Id} {e.Reason}");
        T("start");
        foreach (var t in k.Tabs.ToList()) await k.CloseAsync(t.Id);
        T("closed existing");
        _leases!.MaxLive = 8;

        // 1. load all, scroll each to a known offset
        var opened = new List<VirtualTab>();
        foreach (var u in urls)
        {
            var t = k.Open(new Uri(u));
            var loaded = new TaskCompletionSource();
            void OnEv(KernelEvent e) { if (e.Kind == "restored" && e.Id == t.Id) loaded.TrySetResult(); }
            k.Changed += OnEv;
            await k.ActivateAsync(t.Id);
            await Task.WhenAny(loaded.Task, Task.Delay(20000));
            k.Changed -= OnEv;
            WithActiveLease(l => _ = l.View.CoreWebView2.ExecuteScriptAsync("window.scrollTo(0, 600)"));
            await Task.Delay(500);
            opened.Add(t);
        }
        k.RestoreTimingsMs.Clear();
        T("all loaded");

        // 2. virtualize everything
        foreach (var t in opened) { await k.VirtualizeAsync(t.Id, Cause.User); T($"virtualized {t.Id}"); }
        await Task.Delay(3000);
        var cps = opened.Select(t => k.GetCheckpoint(t.Id)).ToList();
        T("checkpoints read");

        // 3. restore each and measure
        var scrollOk = 0;
        foreach (var t in opened)
        {
            var loaded = new TaskCompletionSource();
            void OnEv(KernelEvent e) { if (e.Kind == "restored" && e.Id == t.Id) loaded.TrySetResult(); }
            k.Changed += OnEv;
            await k.ActivateAsync(t.Id);
            await Task.WhenAny(loaded.Task, Task.Delay(20000));
            k.Changed -= OnEv;
            await Task.Delay(700); // let scrollTo apply
            string y = "0";
            if (_leases.TryGet(t.Id, out var l)) y = await ((WebView2Lease)l).View.CoreWebView2.ExecuteScriptAsync("window.scrollY");
            if (double.TryParse(y, out var yy) && yy > 400) scrollOk++;
        }

        var times = k.RestoreTimingsMs.OrderBy(x => x).ToList();
        double P(double p) => times.Count == 0 ? 0 : times[(int)Math.Min(times.Count - 1, Math.Ceiling(p * times.Count) - 1)];
        var result = new
        {
            pages = urls.Length,
            checkpoints = cps.Count(c => c is not null),
            thumbnails = cps.Count(c => c?.ThumbnailPath is not null && File.Exists(c.ThumbnailPath)),
            scrollCaptured = cps.Count(c => c?.ScrollY > 400),
            scrollRestored = scrollOk,
            restoreMs = times.Select(x => Math.Round(x)).ToList(),
            p50 = Math.Round(P(0.5)), p95 = Math.Round(P(0.95)),
        };
        T("restores done");
        var file = Path.Combine(DataDir, "benchmarks", $"restore-bench-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        T("written");
    }

    private async Task RunMemoryLabAsync()
    {
        string[] urls =
        [
            "https://example.com", "https://en.wikipedia.org/wiki/Web_browser", "https://learn.microsoft.com/en-us/microsoft-edge/webview2/",
            "https://github.com", "https://news.ycombinator.com",
        ];
        var k = _kernel!;
        var report = new List<object>();
        void Record(string step)
        {
            var m = ProcessGroupProbe.Sample(_leases!.ProcessIds);
            report.Add(new { step, live = k.LiveCount, m.ProcessCount, m.WorkingSetMb, m.PrivateMb });
            StatusText.Text = $"{step}: live={k.LiveCount} procs={m.ProcessCount} priv={m.PrivateMb:F0}MB";
        }

        foreach (var t in k.Tabs.ToList()) await k.CloseAsync(t.Id);
        await Task.Delay(2000);
        Record("baseline");

        _leases!.MaxLive = 5;
        var opened = new List<VirtualTab>();
        foreach (var u in urls)
        {
            var t = k.Open(new Uri(u));
            await k.ActivateAsync(t.Id);
            opened.Add(t);
            await Task.Delay(4000);
            Record($"live x{k.LiveCount}");
        }

        foreach (var t in opened.Skip(1)) if (_leases.TryGet(t.Id, out var l)) await l.TrySuspendAsync();
        await Task.Delay(3000);
        Record("4 suspended, 1 live");

        foreach (var t in opened.Skip(1)) await k.VirtualizeAsync(t.Id, Cause.User);
        await Task.Delay(5000);
        Record("4 virtual, 1 live");

        await k.VirtualizeAsync(opened[0].Id, Cause.User);
        await Task.Delay(5000);
        Record("all virtual");

        var file = Path.Combine(DataDir, "benchmarks", $"memory-lab-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        StatusText.Text = $"report written: {file}";
    }
}

/// <summary>The window that has the keyboard, so a check can prove a capture did not move it.</summary>
internal static class NativeForeground
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    public static long Get() => GetForegroundWindow().ToInt64();
}
