using System.Collections.ObjectModel;
using System.Text.Json;
using JevBrowse.App.Renderer;
using JevBrowse.App.Shield;
using JevBrowse.Diagnostics;
using JevBrowse.Shield;
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

    private CoreWebView2Environment? _env;
    private WebView2LeaseManager? _leases;
    private BrowserDb? _db;
    private TabKernel? _kernel;
    private readonly DefaultScheduler _scheduler = new();
    private ShieldAdapter? _shield;
    private FilterListStore? _filters;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tick;
    private ResourcePlan? _lastPlan;
    private bool _syncingSelection;

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _db?.Dispose();
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        var udf = Path.Combine(DataDir, "profiles", "personal");
        Directory.CreateDirectory(udf);
        _env = await CoreWebView2Environment.CreateWithOptionsAsync(null, udf, new CoreWebView2EnvironmentOptions());
        _leases = new WebView2LeaseManager(WebHost, _env, Path.Combine(DataDir, "thumbnails")) { MaxLive = 5 };
        _db = new BrowserDb(Path.Combine(DataDir, "db", "browser.db"));

        // Shield: compile whatever lists are on disk before the first renderer exists; fetch lists if there are none.
        _filters = new FilterListStore(Path.Combine(DataDir, "filters"));
        _shield = new ShieldAdapter(_env, new SiteSettingsRepository(_db));
        _leases.OnCoreCreated = _shield.Attach;
        _leases.OnCoreDisposed = _shield.Detach;
        if (!_filters.HasActiveLists && Environment.GetEnvironmentVariable("JEVBROWSE_NO_FILTER_UPDATE") is null)
            await UpdateFilterListsAsync();
        else
            CompileFilters();

        _kernel = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.Combine(DataDir, "thumbnails"));
        _kernel.Changed += OnKernelChanged;
        _kernel.Load();
        RebuildList();

        foreach (var m in Enum.GetValues<MemoryMode>()) ModeBox.Items.Add(m.ToString());
        ModeBox.SelectedIndex = (int)MemoryMode.Balanced;

        // Resource OS tick: sample → evaluate → apply. 10 s is coarse on purpose; user actions never wait for it.
        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(10);
        _tick.Tick += async (_, _) => await SchedulerTickAsync();
        _tick.Start();

        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--memory-lab") || args.Contains("--restore-bench") || args.Contains("--shield-check"))
        {
            Directory.CreateDirectory(Path.Combine(DataDir, "benchmarks"));
            try
            {
                if (args.Contains("--memory-lab")) await RunMemoryLabAsync();
                else if (args.Contains("--restore-bench")) await RunRestoreBenchAsync();
                else await RunShieldCheckAsync();
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
        if (e.Kind is "opened" or "closed" or "loaded") RebuildList();
        else foreach (var i in Items) i.Refresh();

        if (e.Kind == "activated")
        {
            _syncingSelection = true;
            TabList.SelectedItem = Items.FirstOrDefault(i => i.Id == e.Id);
            _syncingSelection = false;
            AddressBox.Text = _kernel!.Active?.Url.ToString() ?? "";
        }
        VirtualPlaceholder.Visibility = _kernel!.Active is null ? Visibility.Visible : Visibility.Collapsed;
        UpdatePoolText();
        StatusText.Text = $"{e.Kind} {e.Reason}";
    }

    private void RebuildList()
    {
        Items.Clear();
        foreach (var t in _kernel!.Tabs) Items.Add(new TabItem(t));
    }

    private void UpdatePoolText()
    {
        var s = ProcessGroupProbe.Sample(_leases!.ProcessIds);
        var band = _lastPlan is null ? "" : $" • {_lastPlan.Band} band, budget {_lastPlan.TargetLiveRenderers}";
        var blocked = _shield is null ? 0 : _shield.Stats.Values.Sum(x => x.Blocked);
        PoolText.Text = $"{_kernel!.Tabs.Count} tabs • {_kernel.LiveCount}/{_leases.MaxLive} live{band}\n{s.ProcessCount} procs • {s.PrivateMb:F0} MB private (measured)\nShield: {blocked} blocked this session";
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
