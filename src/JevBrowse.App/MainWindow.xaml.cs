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
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tick;
    private ResourcePlan? _lastPlan;
    private bool _syncingSelection;

    public MainWindow()
    {
        InitializeComponent();
        // One layered surface: Mica behind everything, our own title bar, dark by default.
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        // Final semantic checkpoint (bounded to 4 s) so the next start restores the pages the user was on.
        AppWindow.Closing += async (_, e) =>
        {
            if (_shutdownCheckpointDone || _kernel is null) return;
            e.Cancel = true;
            try { await _kernel.CheckpointAllAsync(new CancellationTokenSource(TimeSpan.FromSeconds(4)).Token); }
            catch (Exception) { }
            _shutdownCheckpointDone = true;
            Close();
        };
        Closed += (_, _) => { _agentHost?.Dispose(); _leases?.Shutdown(); _db?.Dispose(); };
        _ = InitAsync();
    }

    private bool _shutdownCheckpointDone;

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
        _db = new BrowserDb(Path.Combine(DataDir, "db", "browser.db"));
        _siteSettings = new SiteSettingsRepository(_db);

        // Shield: compile whatever lists are on disk before the first renderer exists; fetch lists if there are none.
        _filters = new FilterListStore(Path.Combine(DataDir, "filters"));
        _shield = new ShieldAdapter(_siteSettings);
        // Trust OS: permission prompts are owned by the window; policy decides most without UI.
        _permissions = new PermissionAdapter(new SitePermissionsRepository(_db), PromptPermissionAsync);
        // DevSpace: optional module. Off unless JEVBROWSE_DEVSPACE=1 or toggled in the Dev panel; attaches nothing when off.
        _dev = new DevSpaceAdapter(Path.Combine(DataDir, "devspace", "projects.json"));
        _dev.SetEnabled(Environment.GetEnvironmentVariable("JEVBROWSE_DEVSPACE") == "1");
        _leases.OnCoreCreated = async (core, id, container, isolation, url) => { await _shield.AttachAsync(core, id, url); _permissions.Attach(core, container, isolation); _dev.Attach(core, id); };
        _shield.WallDetected += id => DispatcherQueue.TryEnqueue(() =>
        {
            var t = _kernel?.Tabs.FirstOrDefault(x => x.Id == id);
            StatusText.Text = $"{t?.Url.Host ?? "site"} showed an anti-adblock wall. Shield panel → 'Disable for site' if you need the page; Shield stays honest about what it can and cannot do.";
        });
        _leases.OnCoreDisposed = id => { _shield.Detach(id); _dev.Detach(id); };
        if (!_filters.HasActiveLists && Environment.GetEnvironmentVariable("JEVBROWSE_NO_FILTER_UPDATE") is null)
            await UpdateFilterListsAsync();
        else
            CompileFilters();

        // JevBrain: off by default; providers exist only if their keys are in the environment.
        _decisions = new DecisionLogRepository(_db);
        _providers = OpenAiCompatibleProvider.FromEnvironment();
        _jev = JevDecisionProvider.FromEnvironment();
        _brain = new BrainRouter(new DefaultTrustPolicy(), _brainPolicy, _providers,
            d => _decisions.Append(d.At, string.IsNullOrEmpty(d.Task) ? "?" : d.Task + (d.Automatic ? " (automatic)" : " (explicit)"), d.Source.ToString(), d.Rule, d.Model, string.IsNullOrEmpty(d.DataClassName) ? "?" : d.DataClassName, d.Redacted, d.RedactionCount, d.InputChars, d.Output, d.Version),
            decisions: _jev);

        var classifier = new DataClassifier(site => _siteSettings.DataClassOverride(site) is { } c ? (DataClass)c : null);
        _kernel = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.Combine(DataDir, "thumbnails"), null, new WorkspaceRepository(_db), new DefaultTrustPolicy(), classifier);
        _kernel.Changed += OnKernelChanged;
        _kernel.Load();
        RebuildWorkspaces();
        RebuildList();

        // Agent Gateway: in-process always; the loopback HTTP host is opt-in from the Agents panel.
        var auditDir = Path.Combine(DataDir, "agents", "audit");
        Directory.CreateDirectory(auditDir);
        _agents = new AgentGateway.AgentGateway(_kernel, _leases, Path.Combine(DataDir, "agents", "screenshots"), ConfirmAgentActionAsync,
            (s, e) => File.AppendAllText(Path.Combine(auditDir, s.Id + ".jsonl"), JsonSerializer.Serialize(new { e.At, s.Manifest.Agent, e.Action, e.Target, e.Allowed, e.Reason }) + "\n"));

        // Browser Memory: indexes only what Trust OS allows (PUBLIC by default); 200 MB budget.
        _memory = new BrowserMemory(_db);
        _indexer = new MemoryIndexer(_kernel, _leases, _memory);
        _indexer.Decided += (_, why) => DispatcherQueue.TryEnqueue(() => StatusText.Text = "memory: " + why);

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

        // Resource OS tick: sample → evaluate → apply. 10 s is coarse on purpose; user actions never wait for it.
        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(10);
        _tick.Tick += async (_, _) => await SchedulerTickAsync();
        _tick.Start();

        var args = Environment.GetCommandLineArgs();
        _leases.LocalPage = u => u.Scheme == "jev" && u.Host == "welcome" ? WelcomePage.Html(_providers.Any(p => p.IsConfigured), _jev?.IsConfigured == true) : null;

        if (args.Contains("--ui-shot"))
        {
            // Render the window itself (not the screen) after the welcome page and tips settle, for docs and review.
            _ = Task.Run(async () =>
            {
                await Task.Delay(9000);
                DispatcherQueue.TryEnqueue(async () =>
                {
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
                    Application.Current.Exit();
                });
            });
        }

        if (args.Contains("--memory-lab") || args.Contains("--restore-bench") || args.Contains("--shield-check") || args.Contains("--memory-check") || args.Contains("--youtube-check") || args.Contains("--privacy-check") || args.Contains("--agent-check") || args.Contains("--media-check"))
        {
            Directory.CreateDirectory(Path.Combine(DataDir, "benchmarks"));
            try
            {
                if (args.Contains("--media-check")) await RunMediaCheckAsync();
                else if (args.Contains("--agent-check")) await RunAgentCheckAsync();
                else if (args.Contains("--privacy-check")) await RunPrivacyCheckAsync();
                else if (args.Contains("--memory-lab")) await RunMemoryLabAsync();
                else if (args.Contains("--restore-bench")) await RunRestoreBenchAsync();
                else if (args.Contains("--shield-check")) await RunShieldCheckAsync();
                else if (args.Contains("--youtube-check")) await RunYouTubeCheckAsync();
                else await RunMemoryCheckAsync();
            }
            catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "bench-error.txt"), ex.ToString()); }
            Application.Current.Exit();
            return;
        }

        var firstRun = !FirstRunDone();
        if (_kernel.Tabs.Count == 0) _kernel.Open(new Uri(firstRun ? WelcomePage.Url : "https://example.com"));
        await _kernel.ActivateAsync(_kernel.Tabs[0].Id);
        if (firstRun) { await Task.Delay(800); await ShowFirstRunTipsAsync(); }
    }

    // ---- kernel → UI ----

    private void OnKernelChanged(KernelEvent e)
    {
        if (e.Kind is "workspace-created" or "context-restored") RebuildWorkspaces();
        if (e.Kind is "workspace-switched") { SyncWorkspaceBox(); RebuildList(); }
        else if (e.Kind is "opened" or "closed" or "loaded" or "moved" or "context-restored" or "pinned") RebuildList();
        else foreach (var i in Items) i.Refresh();

        if (e.Kind == "activated")
        {
            _syncingSelection = true;
            TabList.SelectedItem = Items.FirstOrDefault(i => i.Id == e.Id);
            _syncingSelection = false;
            AddressBox.Text = _kernel!.Active?.Url.ToString() ?? "";
        }
        if (e.Kind is "activated" or "navigated" or "signals") { UpdateClassBadge(); UpdateEnvChrome(); }
        if (e.Kind is "activated" or "pinned" or "protection" or "loaded") UpdateTabControls();
        if (e.Kind == "restoring") ShowRestoring(e.Id, e.Reason.StartsWith("with"));
        if (e.Kind is "restored" or "loaded") FinishRestore(e.Id);
        UpdateIdlePanel();
        UpdatePoolText();
        StatusText.Text = $"{e.Kind} {e.Reason}";
    }

    /// <summary>
    /// The two tab controls say what they will do next, not what state the tab is in. "Pin" / "Unpin" is placement;
    /// "Keep active" / "Let it sleep" is sleeping. Neither caption implies the other.
    /// </summary>
    private void UpdateTabControls()
    {
        var t = _kernel?.Active;
        PinButton.IsEnabled = KeepActiveButton.IsEnabled = t is not null;
        if (t is null) return;
        PinButton.Content = t.IsPinned ? "Unpin" : "Pin";
        KeepActiveButton.Content = t.UserProtection.HasFlag(ProtectionFlags.KeepActive) ? "Let it sleep" : "Keep active";
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
        foreach (var w in _kernel!.Workspaces) WorkspaceBox.Items.Add($"{w.Name} · {w.Container} ({_kernel.TabsIn(w.Id).Count()})");
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
        var dlg = new ContentDialog { Title = "New workspace", Content = new StackPanel { Spacing = 8, Children = { box, container } }, PrimaryButtonText = "Create", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
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
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || list.SelectedIndex < 0) return;
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
        var band = _lastPlan is null ? "" : $" • {_lastPlan.Band} band, budget {_lastPlan.TargetLiveRenderers}";
        var blocked = _shield?.SessionBlockedTotal ?? 0;
        PoolText.Text = $"{_kernel!.Tabs.Count} tabs • {_kernel.LiveCount}/{_leases.MaxLive} live{band}\n{s.ProcessCount} procs • {s.PrivateMb:F0} MB private (measured)\nShield: {blocked} blocked this session";
    }

    // ---- Restore experience ----

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _restoreTimer;
    private ResourceId? _restoringId;

    /// <summary>Nothing selected, or the selected tab is back: the panel says which, and never sits there lying.</summary>
    private void UpdateIdlePanel()
    {
        ClearStaleRestoreMessage();
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
        else if (_restoringId is null) RestorePanel.Visibility = Visibility.Collapsed;
    }

    private void ShowRestoring(ResourceId id, bool hasSavedPlace)
    {
        var tab = _kernel?.Tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return;
        _restoringId = id;
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
        RestorePanel.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
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
        AddressBox.Text = tab.Url.ToString();
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
        ClassBadgeText.Text = $"{_kernel.ContainerOf(t).ToString().ToUpperInvariant()} • {ClassLabel(cls).ToUpperInvariant()}";
        ClassBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(cls switch
        {
            DataClass.Public => Microsoft.UI.Colors.DarkSeaGreen,
            DataClass.Unknown => Microsoft.UI.Colors.SlateGray,
            DataClass.Authenticated => Microsoft.UI.Colors.SteelBlue,
            DataClass.Sensitive => Microsoft.UI.Colors.DarkOrange,
            DataClass.Secret => Microsoft.UI.Colors.Firebrick,
            _ => Microsoft.UI.Colors.SlateGray,
        });
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
        _ => "This session is private: nothing is written to disk.",
    };

    private async void OnClassBadgeTapped(object s, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (_kernel?.Active is not { } t) return;
        var site = DataClassifier.Site(t.Url.Host);
        var current = _kernel.ClassOf(t);
        // Bound to the enum values, not to positions: adding a class must not silently re-point saved overrides.
        var choices = new DataClass?[] { null, DataClass.Public, DataClass.Authenticated, DataClass.Sensitive };
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in choices) box.Items.Add(c is null ? "Let JevBrowse decide" : ClassLabel(c.Value));
        var over = _siteSettings!.DataClassOverride(site);
        box.SelectedIndex = Math.Max(0, Array.FindIndex(choices, c => (int?)c == over));
        var dlg = new ContentDialog
        {
            Title = $"How should {site} be treated?",
            Content = new StackPanel { Spacing = 8, Children = {
                new TextBlock { Text = $"Now: {ClassLabel(current)}. {ClassExplanation(current)}", TextWrapping = TextWrapping.Wrap },
                box,
                new TextBlock { Text = "A page asking for a password or card number is always treated as Secret, whatever you choose here.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 } } },
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _siteSettings.SetDataClassOverride(site, (int?)choices[Math.Max(0, box.SelectedIndex)]);
        UpdateClassBadge();
    }

    /// <summary>Set only by the --media-check bench, which runs with nobody present to answer a prompt.</summary>
    private bool _autoAllowPermissions;

    private async Task<PermissionAdapter.Choice> PromptPermissionAsync(string site, PermissionKind kind)
    {
        if (_autoAllowPermissions) return PermissionAdapter.Choice.AllowOnce;
        var tcs = new TaskCompletionSource<PermissionAdapter.Choice>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            var dlg = new ContentDialog
            {
                Title = $"{site} wants {kind}",
                Content = new TextBlock { Text = "Grants can be temporary. Denied by default if you close this.", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Allow for 1 hour", SecondaryButtonText = "Allow once", CloseButtonText = "Block", XamlRoot = Content.XamlRoot,
            };
            var r = await dlg.ShowAsync();
            tcs.TrySetResult(r switch
            {
                ContentDialogResult.Primary => PermissionAdapter.Choice.AllowForHour,
                ContentDialogResult.Secondary => PermissionAdapter.Choice.AllowOnce,
                _ => PermissionAdapter.Choice.Block,
            });
        });
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
            tcs.TrySetResult(await dlg.ShowAsync() == ContentDialogResult.Primary);
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
            var body = new StackPanel { Spacing = 12, Children = {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") },
                reads } };
            var dlg = new ContentDialog { Title = "Agent session request", Content = new ScrollViewer { MaxHeight = 460, Content = body }, PrimaryButtonText = "Allow session", CloseButtonText = "Deny", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
            tcs.TrySetResult(await dlg.ShowAsync() == ContentDialogResult.Primary);
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
            .Select(a => new CheckBox { Content = a.ToString(), Tag = a, IsChecked = a is AgentAction.Navigate or AgentAction.Read, IsEnabled = !running }).ToList();
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var b in actionBoxes) actionRow.Children.Add(b);
        var minutes = new NumberBox { Header = "Max minutes per session", Value = 60, Minimum = 1, Maximum = 480, IsEnabled = !running };
        var panel = new StackPanel { Spacing = 8, Children = { toggle } };
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
                panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { stop, state } });
            }
            var stopAll = new Button { Content = "Stop all sessions", Style = (Style)Application.Current.Resources["JevToolButton"] };
            stopAll.Click += async (_, _) => { var n = await _agentHost!.StopAllAsync(); StatusText.Text = $"revoked {n} agent session(s)"; };
            panel.Children.Add(stopAll);
        }
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, Text = "Agents get a manifest-scoped session: allowed domains, allowed actions, data-class ceiling, a live-page quota, and a time/action budget. Destructive clicks ask you. Every request is written to data/agents/audit/<session>.jsonl. See docs/AGENT_SECURITY.md." });
        var dlg = new ContentDialog { Title = "Agent Gateway", Content = new ScrollViewer { MaxHeight = 480, Content = panel }, PrimaryButtonText = "Apply", CloseButtonText = "Close", XamlRoot = Content.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
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
            _agentHost = new LocalAgentHost(_agents, ceiling, ApproveAgentSessionAsync);
            _agentHost.Start();
            StatusText.Text = $"agent endpoint listening on 127.0.0.1:{_agentHost.Port} (token in Agents panel); grant limited to {string.Join(", ", ceiling.Limits.AllowDomains)}";
        }
        else if (!toggle.IsOn && running)
        {
            _agentHost!.Dispose(); _agentHost = null;
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
        var color = r.Environment switch
        {
            DeployEnvironment.Prod => Windows.UI.Color.FromArgb(0xE0, 0xFF, 0x3B, 0x30),
            DeployEnvironment.Staging => Windows.UI.Color.FromArgb(0xE0, 0xFF, 0x95, 0x00),
            DeployEnvironment.Dev => Windows.UI.Color.FromArgb(0xE0, 0x00, 0x7A, 0xFF),
            _ => Windows.UI.Color.FromArgb(0xE0, 0x34, 0xC7, 0x59),
        };
        EnvBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        ProdBorder.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        ProdBorder.Visibility = r.Environment == DeployEnvironment.Prod ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnDev(object s, RoutedEventArgs e)
    {
        if (_dev is null || _kernel is null) return;
        var enable = new ToggleSwitch { Header = "DevSpace enabled (attaches DevTools listeners to new renderers)", IsOn = _dev.Enabled };
        var panel = new StackPanel { Spacing = 8, Children = { enable } };

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
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
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
            Content = new StackPanel { Spacing = 8, Children = { box, info, results } },
            PrimaryButtonText = "Open", SecondaryButtonText = "Clear index", CloseButtonText = "Close", XamlRoot = Content.XamlRoot,
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            var confirm = new ContentDialog { Title = "Clear Browser Memory?", Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"This deletes the local index of {docs} pages. It does not touch your tabs, history or cookies. Pages you read later are indexed again." }, PrimaryButtonText = "Clear", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary) { var n = _memory.Clear(); StatusText.Text = $"Browser Memory cleared ({n} pages)"; }
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
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        StatusText.Text = $"asking {provider!.Kind}…";
        var d = await _brain.DecideAsync(new DecisionRequest(BrainTask.SummarizePage, text, cls, container, ExplicitUserAction: true, t.Url), default);
        var result = new ContentDialog
        {
            Title = d.WasDenied ? "Not answered" : $"Summary via {d.Source} ({d.Model})",
            Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = d.WasDenied ? $"Rule: {d.Rule}" : d.Output, TextWrapping = TextWrapping.Wrap } },
            CloseButtonText = "Close", XamlRoot = Content.XamlRoot,
        };
        StatusText.Text = $"brain: {d.Rule}{(d.Redacted ? $" • {d.RedactionCount} redacted" : "")}";
        await result.ShowAsync();
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
        var panel = new StackPanel { Spacing = 8, Children = { ai, cloud, auto, providers, metric, new TextBlock { Text = "Decision log (newest first):", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, new ScrollViewer { MaxHeight = 260, Content = log } } };
        var dlg = new ContentDialog { Title = "JevBrain", Content = panel, PrimaryButtonText = "Save", CloseButtonText = "Close", XamlRoot = Content.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
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

    private async void OnShield(object s, RoutedEventArgs e)
    {
        if (_kernel?.Active is not { } t || _shield is null) return;
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
        var undoNote = new TextBlock { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        showHidden.Click += async (_, _) =>
        {
            if (_leases!.TryGet(t.Id, out var l)) { var n = await _shield.RestoreHiddenAsync(((WebView2Lease)l).View.CoreWebView2, t.Id); undoNote.Text = n > 0 ? $"Restored {n} hidden item(s). Nothing more is hidden until you navigate." : "Nothing on this page was hidden."; }
        };
        var dlg = new ContentDialog
        {
            Title = "Shield",
            Content = new StackPanel { Spacing = 8, Children = { showHidden, undoNote, new ScrollViewer { Content = new TextBlock { Text = string.Join("\n", lines), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap }, MaxHeight = 360 } } },
            PrimaryButtonText = enabled ? $"Disable for {site}" : $"Enable for {site}",
            SecondaryButtonText = "Update lists",
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await _shield.SetEnabledForAsync(site, !enabled);   // removes cosmetic + site-module scripts in every open tab of this site
            WithActiveLease(l => l.View.CoreWebView2.Reload());
            StatusText.Text = $"Shield {(enabled ? "disabled" : "enabled")} for {site}; page reloaded";
        }
        else if (result == ContentDialogResult.Secondary) await UpdateFilterListsAsync();
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
        using var host = new LocalAgentHost(_agents!, ceiling);
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
        if (!_leases!.TryGet(tab.Id, out var lease)) return;
        var core = ((WebView2Lease)lease).View.CoreWebView2;

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
              sharedArrayBuffer: typeof SharedArrayBuffer === 'function',
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
        var protectionDuringCall = "not observed";
        try
        {
            _autoAllowPermissions = true;
            var opened = await EvalAsync("window.__jevCall = await navigator.mediaDevices.getUserMedia({audio:true, video:true}); return { ok: true };", 30000);
            if (opened.TryGetProperty("ok", out _))
            {
                await Task.Delay(2500);
                var demote = await k.VirtualizeAsync(tab.Id, Cause.Scheduler);
                protectionDuringCall = $"{tab.Protection} → scheduler {(demote.Allowed ? "PUT IT TO SLEEP" : "refused: " + demote.Reason)}";
                // If it did get put to sleep the renderer is gone; touching it again would throw.
                if (!demote.Allowed) await EvalAsync("window.__jevCall.getTracks().forEach(t => t.stop()); return { ok: true };", 10000);
            }
            else protectionDuringCall = "no media to hold: " + opened;
        }
        catch (Exception ex) { protectionDuringCall = "probe failed: " + ex.GetType().Name; }
        finally { _autoAllowPermissions = false; }

        bool B(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
        var stackWorks = B(apis, "getUserMedia") && B(apis, "RTCPeerConnection") && B(loopback, "connected") && B(loopback, "remoteTrackReceived");
        var framesFlowed = loopback.TryGetProperty("framesDecoded", out var fd) && fd.GetInt32() > 0;

        // A call that the scheduler is free to hibernate is not a working call, so protection is part of PASS.
        var callSurvivesScheduler = protectionDuringCall.Contains("refused", StringComparison.Ordinal);
        var result = new
        {
            pass = stackWorks && framesFlowed && callSurvivesScheduler,
            summary = !stackWorks ? "The WebRTC stack did not complete a loopback call."
                : !framesFlowed ? "WebRTC negotiated but no frames decoded."
                : !callSurvivesScheduler ? "WebRTC works, but the scheduler is willing to hibernate a live call."
                : "WebRTC negotiates and carries video end to end, and a live call blocks hibernation.",
            apiSurface = apis,
            loopbackCall = loopback,
            realDevices = devices,
            sleepDuringCall = protectionDuringCall,
            note = "Permission prompts were auto-allowed for this run; in normal use camera and microphone are Ask. "
                 + "Device counts of 0 mean this machine has no camera/microphone attached, not that JevBrowse blocked them.",
        };
        var file = Path.Combine(DataDir, "benchmarks", $"media-check-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
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
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        }
        _kernel.SetProtection(t.Id, on ? t.UserProtection | ProtectionFlags.KeepActive : t.UserProtection & ~ProtectionFlags.KeepActive);
        StatusText.Text = on ? "Keeping this tab active: it will not sleep on its own." : "This tab can sleep when memory is needed.";
        foreach (var i in Items) i.Refresh();
    }

    private async void OnExplain(object s, RoutedEventArgs e)
    {
        if (_kernel?.Active is not { } t) return;
        var d = _kernel.LastDecision(t.Id);
        var text = d is null
            ? "The scheduler has not made a decision about this tab yet.\n\n" +
              (_lastPlan is null ? "" : $"Current band: {_lastPlan.Band}, live budget {_lastPlan.TargetLiveRenderers}, live now {_lastPlan.LiveNow}.")
            : d.Explain();
        var dlg = new ContentDialog
        {
            Title = "Why?",
            Content = new TextBlock { Text = text, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "Close",
            // The action offered here is about sleeping, because that is what "Why?" just explained. Placement is a
            // different question and belongs to the Pin button.
            PrimaryButtonText = t.UserProtection.HasFlag(ProtectionFlags.KeepActive) ? "Let it sleep" : "Keep this tab active",
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary) OnKeepActiveCurrent(s, e);
    }

    // ---- UI → kernel ----

    private async void OnTabSelected(object s, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || TabList.SelectedItem is not TabItem item || _kernel is null) return;
        await _kernel.ActivateAsync(item.Id);
    }

    private async void OnNewTab(object s, RoutedEventArgs e)
    {
        var t = _kernel!.Open(new Uri("https://duckduckgo.com"));
        await _kernel.ActivateAsync(t.Id);
        AddressBox.Focus(FocusState.Programmatic);
        AddressBox.SelectAll();
    }

    private async void OnCloseTab(object s, RoutedEventArgs e)
    {
        if ((s as Button)?.Tag is not ResourceId id) return;
        var wasActive = _kernel!.Active?.Id == id;
        await _kernel.CloseAsync(id);
        if (wasActive && _kernel.Tabs.Count > 0) await _kernel.ActivateAsync(_kernel.Tabs[^1].Id);
    }

    private async void OnHibernateCurrent(object s, RoutedEventArgs e)
    {
        if (_kernel?.Active is not { } tab) return;
        // A manual request overrides protections, so say what is being overridden before dropping the renderer.
        if (tab.IsDemotionVetoed)
        {
            var dlg = new ContentDialog
            {
                Title = "This tab is protected",
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"Protection: {tab.Protection}.\nHibernating discards the live page. Anything not saved on the site (a typed form, an upload, a call, a download in progress) will be lost; only the address and scroll position are kept." },
                PrimaryButtonText = "Hibernate anyway", CloseButtonText = "Keep it live", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
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

    private async void OnAddressKeyDown(object s, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _kernel is null) return;
        var t = AddressBox.Text.Trim();
        if (!t.Contains("://")) t = t.Contains('.') && !t.Contains(' ')
            ? "https://" + t
            : "https://duckduckgo.com/?q=" + Uri.EscapeDataString(t);
        var url = new Uri(t);
        if (_kernel.Active is null) { var nt = _kernel.Open(url); await _kernel.ActivateAsync(nt.Id); return; }
        WithActiveLease(l => l.Navigate(url));
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
