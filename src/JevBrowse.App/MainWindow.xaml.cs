using System.Text.Json;
using JevBrowse.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace JevBrowse.App;

/// <summary>
/// Phase 0 (M0 Feasibility) shell: one WebView2 + a "Memory Lab" that answers the question the whole
/// architecture rests on: how much RAM does disposing a renderer actually give back?
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly string DataDir =
        Environment.GetEnvironmentVariable("JEVBROWSE_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JevBrowse");

    private CoreWebView2Environment? _env;
    private WebView2? _view;

    public MainWindow()
    {
        InitializeComponent();
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        // User-data folder lives under the data dir (D: in dev) so profiles never land on C:.
        var udf = Path.Combine(DataDir, "profiles", "personal");
        Directory.CreateDirectory(udf);
        _env = await CoreWebView2Environment.CreateWithOptionsAsync(null, udf, new CoreWebView2EnvironmentOptions());
        await AttachNewViewAsync("https://example.com");
    }

    private async Task<WebView2> AttachNewViewAsync(string url)
    {
        var v = new WebView2();
        WebHost.Children.Add(v);
        await v.EnsureCoreWebView2Async(_env);
        v.CoreWebView2.SourceChanged += (_, _) => AddressBox.Text = v.Source?.ToString() ?? "";
        v.CoreWebView2.Navigate(url);
        _view = v;
        Status($"live renderer attached. {Sample()}");
        return v;
    }

    private string Sample()
    {
        if (_env is null) return "no env";
        var pids = _env.GetProcessInfos().Select(p => p.ProcessId);
        var s = ProcessGroupProbe.Sample(pids);
        return $"procs={s.ProcessCount} ws={s.WorkingSetMb:F0}MB private={s.PrivateMb:F0}MB (measured)";
    }

    private void Status(string msg) => StatusText.Text = msg;

    private void OnBack(object s, RoutedEventArgs e) { if (_view?.CanGoBack == true) _view.GoBack(); }
    private void OnForward(object s, RoutedEventArgs e) { if (_view?.CanGoForward == true) _view.GoForward(); }

    private void OnAddressKeyDown(object s, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _view?.CoreWebView2 is null) return;
        var t = AddressBox.Text.Trim();
        if (!t.Contains("://")) t = t.Contains('.') && !t.Contains(' ')
            ? "https://" + t
            : "https://duckduckgo.com/?q=" + Uri.EscapeDataString(t);
        _view.CoreWebView2.Navigate(t);
    }

    /// <summary>Dispose = the lease is released; this is what VIRTUAL state will rely on.</summary>
    private void OnDispose(object s, RoutedEventArgs e)
    {
        if (_view is null) { Status("no live renderer (virtual)."); return; }
        var before = Sample();
        DisposeView(_view);
        _view = null;
        Status($"disposed. before: {before}");
    }

    private void DisposeView(WebView2 v)
    {
        WebHost.Children.Remove(v);
        v.Close();
    }

    private async void OnMemoryLab(object s, RoutedEventArgs e)
    {
        LabButton.IsEnabled = false;
        try { await RunMemoryLabAsync(); }
        catch (Exception ex) { Status("lab failed: " + ex.Message); }
        finally { LabButton.IsEnabled = true; }
    }

    private async Task RunMemoryLabAsync()
    {
        string[] urls =
        [
            "https://example.com", "https://en.wikipedia.org/wiki/Web_browser", "https://learn.microsoft.com/en-us/microsoft-edge/webview2/",
            "https://github.com", "https://news.ycombinator.com",
        ];

        if (_view is not null) { DisposeView(_view); _view = null; }
        await Task.Delay(2000);
        var report = new List<object>();
        void Record(string step) { var m = ProcessGroupProbe.Sample(_env!.GetProcessInfos().Select(p => p.ProcessId)); report.Add(new { step, m.ProcessCount, m.WorkingSetMb, m.PrivateMb }); Status($"{step}: procs={m.ProcessCount} ws={m.WorkingSetMb:F0}MB priv={m.PrivateMb:F0}MB"); }

        Record("baseline (0 views)");
        var views = new List<WebView2>();
        foreach (var u in urls)
        {
            var v = new WebView2();
            WebHost.Children.Add(v);
            await v.EnsureCoreWebView2Async(_env);
            var done = new TaskCompletionSource();
            v.CoreWebView2.NavigationCompleted += (_, _) => done.TrySetResult();
            v.CoreWebView2.Navigate(u);
            await Task.WhenAny(done.Task, Task.Delay(20000));
            views.Add(v);
            Record($"live x{views.Count}");
        }
        await Task.Delay(3000);
        Record("5 live, settled");

        foreach (var v in views.Skip(1)) { if (v.CoreWebView2 is not null) await v.CoreWebView2.TrySuspendAsync(); }
        await Task.Delay(3000);
        Record("4 suspended, 1 live");

        foreach (var v in views.Skip(1)) DisposeView(v);
        GC.Collect(); GC.WaitForPendingFinalizers();
        await Task.Delay(5000);
        Record("4 disposed, 1 live");

        DisposeView(views[0]);
        await Task.Delay(5000);
        Record("all disposed (virtual)");

        var dir = Path.Combine(DataDir, "benchmarks");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"memory-lab-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Status($"report written: {file}");
        await AttachNewViewAsync("https://example.com");
    }
}
