using System.Collections.ObjectModel;
using System.Text.Json;
using JevBrowse.App.Renderer;
using JevBrowse.Diagnostics;
using JevBrowse.Domain;
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
        _leases = new WebView2LeaseManager(WebHost, _env) { MaxLive = 5 };
        _db = new BrowserDb(Path.Combine(DataDir, "db", "browser.db"));
        _kernel = new TabKernel(_leases, new TabRepository(_db));
        _kernel.Changed += OnKernelChanged;
        _kernel.Load();
        RebuildList();

        if (Environment.GetCommandLineArgs().Contains("--memory-lab"))
        {
            try { await RunMemoryLabAsync(); }
            catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "memory-lab-error.txt"), ex.ToString()); }
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
        PoolText.Text = $"{_kernel!.Tabs.Count} tabs • {_kernel.LiveCount}/{_leases.MaxLive} live\n{s.ProcessCount} procs • {s.PrivateMb:F0} MB private (measured)";
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

    private async Task RunMemoryLabAsync()
    {
        string[] urls =
        [
            "https://example.com", "https://en.wikipedia.org/wiki/Web_browser", "https://learn.microsoft.com/en-us/microsoft-edge/webview2/",
            "https://github.com", "https://news.ycombinator.com",
        ];
        var k = _kernel!;
        Directory.CreateDirectory(Path.Combine(DataDir, "benchmarks"));
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
