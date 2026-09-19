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
        Closed += (_, _) => { _agentHost?.Dispose(); _leases?.Shutdown(); _db?.Dispose(); };
        _ = InitAsync();
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
        _leases.OnCoreCreated = (core, id, _) => { _shield.Attach(core, id); _permissions.Attach(core); _dev.Attach(core, id); };
        _leases.OnCoreDisposed = id => { _shield.Detach(id); _dev.Detach(id); };
        if (!_filters.HasActiveLists && Environment.GetEnvironmentVariable("JEVBROWSE_NO_FILTER_UPDATE") is null)
            await UpdateFilterListsAsync();
        else
            CompileFilters();

        // JevBrain: off by default; providers exist only if their keys are in the environment.
        _decisions = new DecisionLogRepository(_db);
        _providers = OpenAiCompatibleProvider.FromEnvironment();
        _brain = new BrainRouter(new DefaultTrustPolicy(), _brainPolicy, _providers,
            d => _decisions.Append(d.At, "?", d.Source.ToString(), d.Rule, d.Model, "?", d.Redacted, d.RedactionCount, d.InputChars, d.Output, d.Version));

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

        foreach (var m in Enum.GetValues<MemoryMode>()) ModeBox.Items.Add(m.ToString());
        ModeBox.SelectedIndex = (int)MemoryMode.Balanced;
        foreach (var m in Enum.GetValues<ProductMode>()) ProductModeBox.Items.Add(m.ToString());
        ProductModeBox.SelectedIndex = (int)(Enum.TryParse<ProductMode>(Environment.GetEnvironmentVariable("JEVBROWSE_MODE"), true, out var pm) ? pm : ProductMode.Power);

        // Resource OS tick: sample → evaluate → apply. 10 s is coarse on purpose; user actions never wait for it.
        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(10);
        _tick.Tick += async (_, _) => await SchedulerTickAsync();
        _tick.Start();

        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--memory-lab") || args.Contains("--restore-bench") || args.Contains("--shield-check") || args.Contains("--memory-check"))
        {
            Directory.CreateDirectory(Path.Combine(DataDir, "benchmarks"));
            try
            {
                if (args.Contains("--memory-lab")) await RunMemoryLabAsync();
                else if (args.Contains("--restore-bench")) await RunRestoreBenchAsync();
                else if (args.Contains("--shield-check")) await RunShieldCheckAsync();
                else await RunMemoryCheckAsync();
            }
            catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "bench-error.txt"), ex.ToString()); }
            Application.Current.Exit();
            return;
        }

        if (_kernel.Tabs.Count == 0) _kernel.Open(new Uri("https://example.com"));
        await _kernel.ActivateAsync(_kernel.Tabs[0].Id);
    }

    // ---- kernel → UI ----

    private void OnKernelChanged(KernelEvent e)
    {
        if (e.Kind is "workspace-created" or "context-restored") RebuildWorkspaces();
        if (e.Kind is "workspace-switched") { SyncWorkspaceBox(); RebuildList(); }
        else if (e.Kind is "opened" or "closed" or "loaded" or "moved" or "context-restored") RebuildList();
        else foreach (var i in Items) i.Refresh();

        if (e.Kind == "activated")
        {
            _syncingSelection = true;
            TabList.SelectedItem = Items.FirstOrDefault(i => i.Id == e.Id);
            _syncingSelection = false;
            AddressBox.Text = _kernel!.Active?.Url.ToString() ?? "";
        }
        if (e.Kind is "activated" or "navigated" or "signals") { UpdateClassBadge(); UpdateEnvChrome(); }
        VirtualPlaceholder.Visibility = _kernel!.Active is null ? Visibility.Visible : Visibility.Collapsed;
        UpdatePoolText();
        StatusText.Text = $"{e.Kind} {e.Reason}";
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
        VirtualPlaceholder.Visibility = _kernel.Active is null ? Visibility.Visible : Visibility.Collapsed;
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
        var w = _kernel!.CreateWorkspace(box.Text.Trim());
        w.Container = (IdentityContainer)container.SelectedIndex;
        new WorkspaceRepository(_db!).Upsert(w);
        await _kernel.SwitchWorkspaceAsync(w.Id);
        RebuildWorkspaces();
        VirtualPlaceholder.Visibility = Visibility.Visible;
    }

    private void OnMoveMenuOpening(object s, object e)
    {
        MoveMenu.Items.Clear();
        if (_kernel?.Active is not { } t) return;
        foreach (var w in _kernel.Workspaces.Where(w => w.Id != t.WorkspaceId))
        {
            var item = new MenuFlyoutItem { Text = w.Name };
            var target = w.Id;
            item.Click += (_, _) => { _kernel.MoveToWorkspace(t.Id, target); RebuildWorkspaces(); StatusText.Text = $"moved to {w.Name}"; };
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
        var blocked = _shield is null ? 0 : _shield.Stats.Values.Sum(x => x.Blocked);
        PoolText.Text = $"{_kernel!.Tabs.Count} tabs • {_kernel.LiveCount}/{_leases.MaxLive} live{band}\n{s.ProcessCount} procs • {s.PrivateMb:F0} MB private (measured)\nShield: {blocked} blocked this session";
    }

    // ---- Trust OS ----

    private void UpdateClassBadge()
    {
        if (_kernel?.Active is not { } t) { ClassBadgeText.Text = ""; return; }
        var cls = _kernel.ClassOf(t);
        ClassBadgeText.Text = $"{_kernel.ContainerOf(t).ToString().ToUpperInvariant()} • {cls.ToString().ToUpperInvariant()}";
        ClassBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(cls switch
        {
            DataClass.Public => Microsoft.UI.Colors.DarkSeaGreen,
            DataClass.Authenticated => Microsoft.UI.Colors.SteelBlue,
            DataClass.Sensitive => Microsoft.UI.Colors.DarkOrange,
            DataClass.Secret => Microsoft.UI.Colors.Firebrick,
            _ => Microsoft.UI.Colors.SlateGray,
        });
    }

    private async void OnClassBadgeTapped(object s, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (_kernel?.Active is not { } t) return;
        var site = DataClassifier.Site(t.Url.Host);
        var current = _kernel.ClassOf(t);
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        box.Items.Add("Let JevBrowse decide");
        foreach (var c in new[] { DataClass.Public, DataClass.Authenticated, DataClass.Sensitive }) box.Items.Add(c.ToString());
        var over = _siteSettings!.DataClassOverride(site);
        box.SelectedIndex = over is null ? 0 : (int)over + 1;
        var dlg = new ContentDialog
        {
            Title = $"Data class for {site}",
            Content = new StackPanel { Spacing = 8, Children = {
                new TextBlock { Text = $"Currently {current}. Higher classes persist less and never send content to AI. A password field on the page always forces SECRET.", TextWrapping = TextWrapping.Wrap },
                box } },
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _siteSettings.SetDataClassOverride(site, box.SelectedIndex == 0 ? null : box.SelectedIndex - 1);
        UpdateClassBadge();
    }

    private async Task<PermissionAdapter.Choice> PromptPermissionAsync(string site, PermissionKind kind)
    {
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

    private async void OnAgents(object s, RoutedEventArgs e)
    {
        if (_agents is null) return;
        var running = _agentHost?.IsRunning == true;
        var toggle = new ToggleSwitch { Header = "Local endpoint for external agents (127.0.0.1, bearer token, this run only)", IsOn = running };
        var panel = new StackPanel { Spacing = 8, Children = { toggle } };
        if (running)
        {
            var url = $"http://127.0.0.1:{_agentHost!.Port}/";
            panel.Children.Add(new TextBlock { Text = $"Base URL: {url}", IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
            panel.Children.Add(new TextBlock { Text = $"Token:    {_agentHost.Token}", IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
            panel.Children.Add(new TextBlock { Text = "Sessions:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            foreach (var ses in _agentHost.Sessions)
                panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Text = $"{ses.Manifest.Agent} [{ses.Id}] {(ses.Closed ? "closed" : "open")} • {ses.ActionsUsed}/{ses.Manifest.MaxActions} actions • {_agents.LiveAgentPages(ses)}/{ses.Manifest.MaxLivePages} live • expires {ses.ExpiresAt.ToLocalTime():HH:mm}\n" + string.Join("\n", ses.Audit.TakeLast(6).Select(a => $"   {(a.Allowed ? "✓" : "✕")} {a.Action} {a.Target} — {a.Reason}")) });
        }
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, Text = "Agents get a manifest-scoped session: allowed domains, allowed actions, data-class ceiling, a live-page quota, and a time/action budget. Destructive clicks ask you. Every request is written to data/agents/audit/<session>.jsonl. See docs/AGENT_SECURITY.md." });
        var dlg = new ContentDialog { Title = "Agent Gateway", Content = new ScrollViewer { MaxHeight = 480, Content = panel }, PrimaryButtonText = "Apply", CloseButtonText = "Close", XamlRoot = Content.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        if (toggle.IsOn && !running)
        {
            _agentHost = new LocalAgentHost(_agents);
            _agentHost.Start();
            StatusText.Text = $"agent endpoint listening on 127.0.0.1:{_agentHost.Port} (token in Agents panel)";
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
        var info = new TextBlock { Opacity = 0.7, FontSize = 12, Text = $"{docs} pages indexed • {bytes / 1024.0 / 1024.0:F1} MB • local only" };
        void RunSearch()
        {
            hits = _memory.Search(box.Text, 20, _kernel.ActiveWorkspace).ToList();
            results.Items.Clear();
            foreach (var h in hits)
            {
                var open = _kernel.Tabs.Any(t => t.Id == h.Id);
                results.Items.Add(new StackPanel { Children = {
                    new TextBlock { Text = $"{h.Title}  {(open ? "" : "(closed — will reopen)")}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
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
            PrimaryButtonText = "Open", CloseButtonText = "Close", XamlRoot = Content.XamlRoot,
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || results.SelectedIndex < 0 || results.SelectedIndex >= hits.Count) return;
        var hit = hits[results.SelectedIndex];
        if (_kernel.Tabs.Any(t => t.Id == hit.Id)) await _kernel.ActivateAsync(hit.Id);
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
                 : cls >= DataClass.Sensitive ? $"This page is {cls}. Its content never leaves the device (hard rule)."
                 : provider is null ? "No AI provider is configured (set OPENROUTER_API_KEY or JEV_API_KEY in the environment)."
                 : $"Send {redacted.Text.Length:N0} characters of this {cls} page to {provider.Kind} ({provider.Model})?\n" +
                   $"{redacted.Count} item(s) were redacted first{(redacted.Count > 0 ? ": " + string.Join(", ", redacted.Kinds) : "")}.\n" +
                   "The page URL and your identity are not sent.",
        };
        var canSend = _brainPolicy.AiEnabled && cls < DataClass.Sensitive && provider is not null;
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
        var ai = new ToggleSwitch { Header = "AI enabled", IsOn = _brainPolicy.AiEnabled };
        var cloud = new ToggleSwitch { Header = "Cloud providers allowed", IsOn = _brainPolicy.CloudEnabled };
        var providers = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Providers: " + string.Join(", ", _providers.Select(p => $"{p.Kind} {(p.IsConfigured ? "✓ " + p.Model : "(not configured)")}")) };
        var byClass = _decisions!.CloudCallsByClass();
        var metric = new TextBlock { Text = "Cloud calls by data class: " + (byClass.Count == 0 ? "none" : string.Join(", ", byClass.Select(kv => $"{kv.Key}={kv.Value}"))), Opacity = 0.8 };
        var log = new TextBlock { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Text = string.Join("\n", _decisions.Recent(25).Select(r => $"{r.At.ToLocalTime():HH:mm:ss} {r.Source,-10} {r.Rule}{(r.Redacted ? $" (redacted {r.RedactionCount})" : "")}")) };
        var panel = new StackPanel { Spacing = 8, Children = { ai, cloud, providers, metric, new TextBlock { Text = "Decision log (newest first):", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, new ScrollViewer { MaxHeight = 260, Content = log } } };
        var dlg = new ContentDialog { Title = "JevBrain", Content = panel, PrimaryButtonText = "Save", CloseButtonText = "Close", XamlRoot = Content.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _brainPolicy.AiEnabled = ai.IsOn;
        _brainPolicy.CloudEnabled = cloud.IsOn;
        StatusText.Text = $"brain: AI {(ai.IsOn ? "on" : "off")}, cloud {(cloud.IsOn ? "on" : "off")}";
    }

    // ---- Shield ----

    private long _filterCompileMs;
    private FilterEngine? _engine;

    private void CompileFilters()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _engine = FilterEngine.Compile(_filters!.ReadActiveLines());
        _filterCompileMs = sw.ElapsedMilliseconds;
        _shield!.SetEngine(_engine);
        StatusText.Text = $"Shield: {_engine.RuleCount:N0} rules compiled in {_filterCompileMs} ms ({_engine.SkippedLines:N0} unsupported lines skipped)";
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
            st is null ? "no requests seen yet" : $"{st.Blocked} blocked of {st.Total} requests on this page",
            $"{_shield.RuleCount:N0} rules active",
            "",
        };
        if (st is not null) lines.AddRange(st.Recent.Reverse().Take(15).Select(x => $"✕ {x.Host}\n    {x.Rule}"));
        var dlg = new ContentDialog
        {
            Title = "Shield",
            Content = new ScrollViewer { Content = new TextBlock { Text = string.Join("\n", lines), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap }, MaxHeight = 400 },
            PrimaryButtonText = enabled ? $"Disable for {site}" : $"Enable for {site}",
            SecondaryButtonText = "Update lists",
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _shield.SetEnabledFor(site, !enabled);
            WithActiveLease(l => l.View.CoreWebView2.Reload());
            StatusText.Text = $"Shield {(enabled ? "disabled" : "enabled")} for {site}; page reloaded";
        }
        else if (result == ContentDialogResult.Secondary) await UpdateFilterListsAsync();
    }

    /// <summary>Phase 4 gate: load ad-heavy pages with the real engine and report blocked/total per page.</summary>
    private async Task RunShieldCheckAsync()
    {
        string[] urls = ["https://www.theverge.com", "https://www.cnn.com", "https://www.forbes.com", "https://en.wikipedia.org/wiki/Advertising"];
        var k = _kernel!;
        foreach (var t in k.Tabs.ToList()) await k.CloseAsync(t.Id);
        var rows = new List<object>();
        foreach (var u in urls)
        {
            var t = k.Open(new Uri(u));
            await k.ActivateAsync(t.Id);
            await Task.Delay(15000);
            _shield!.Stats.TryGetValue(t.Id, out var st);
            rows.Add(new { url = u, total = st?.Total ?? 0, blocked = st?.Blocked ?? 0, sample = st?.Recent.Take(5).Select(x => x.Host + " ← " + x.Rule).ToList() });
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

    private void OnPinCurrent(object s, RoutedEventArgs e)
    {
        if (_kernel?.Active is not { } t) return;
        var next = t.UserProtection ^ ProtectionFlags.UserPinned;
        _kernel.SetProtection(t.Id, next);
        StatusText.Text = next.HasFlag(ProtectionFlags.UserPinned) ? "pinned: never auto-hibernated" : "unpinned";
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
            PrimaryButtonText = t.UserProtection.HasFlag(ProtectionFlags.UserPinned) ? "Unpin" : "Never hibernate this tab",
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary) OnPinCurrent(s, e);
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
        if (_kernel?.Active is null) return;
        var r = await _kernel.VirtualizeAsync(_kernel.Active.Id, Cause.User);
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
