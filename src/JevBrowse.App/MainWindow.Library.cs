using JevBrowse.App.Renderer;
using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace JevBrowse.App;

/// <summary>
/// The everyday lists: history, downloads, and per-site zoom. What is recorded follows one rule: ordinary workspaces, and pages that are not Sensitive or Secret.
/// Private and agent sessions leave no history and no zoom entry; a Private download appears in a list held in memory only, so it can still be found
/// this session (the file itself stays where it was saved) but nothing about it is written to disk.
/// </summary>
public sealed partial class MainWindow
{
    private HistoryRepository? _history;
    private DownloadRepository? _downloads;
    private SiteZoomRepository? _siteZoom;
    private HistoryRecorder? _historyRecorder;
    // Private-session (or unknown-owner) downloads: memory only, each tied to the workspace it belongs to so ending that session forgets it.
    private readonly List<(DownloadRecord Rec, ContextId Workspace)> _sessionDownloads = [];

    private void InitLibrary()
    {
        _history = new HistoryRepository(_db!);
        _downloads = new DownloadRepository(_db!);
        _siteZoom = new SiteZoomRepository(_db!);
        _historyRecorder = new HistoryRecorder(_kernel!, _history, siteZoom: _siteZoom);
        _leases!.OnChord = (id, chord) => { if (_kernel!.Active?.Id == id) HandleChord(chord); };   // shortcuts pressed while the PAGE has focus
        _leases.OnZoomKey = (id, dir, reset) => { if (_kernel!.Active?.Id == id) Zoom(dir, reset); };   // keys pressed while the PAGE has focus
        _leases.OnDownloadFinished = (id, name, path, source, ok, agent, container, workspace) =>
        {
            if (agent) return;   // an agent's downloads are not the person's; they are governed by the agent's own log
            var rec = new DownloadRecord(name, path, source?.Host ?? "", DateTimeOffset.UtcNow, ok);
            // Ownership was fixed when the page was created, not looked up now: the tab may be closed by the time a download ends. Unknown owner is treated as Private.
            var ordinary = DownloadOwnership.MayPersist(container);
            try { if (!ordinary) _sessionDownloads.Add((rec, workspace)); else _downloads!.Add(rec); } catch (Exception) { }
            if (PanelOpen && _panelId == "downloads") RefreshPanel(force: true);
            StatusText.Text = ok ? $"Downloaded {name}. Ctrl+J shows your downloads." : $"The download of {name} did not finish.";
        };
    }

    /// <summary>Runs the same action the keyboard accelerator would, for a shortcut the page forwarded (the accelerators do not fire while the page has focus).</summary>
    private void HandleChord(string chord)
    {
        try
        {
            switch (chord)
            {
                case "addr": OnFocusAddress(null!, null); break;
                case "newtab": OnNewTabAccelerator(null!, null); break;
                case "closetab": OnCloseTabAccelerator(null!, null); break;
                case "reopen": OnReopenClosedAccelerator(null!, null); break;
                case "palette": OnPaletteAccelerator(null!, null); break;
                case "bookmark": OnBookmarkAccelerator(null!, null); break;
                case "bookmarks": OnBookmarksAccelerator(null!, null); break;
                case "history": OnHistoryAccelerator(null!, null); break;
                case "downloads": OnDownloadsAccelerator(null!, null); break;
                case "next": OnNextTabAccelerator(null!, null); break;
                case "prev": OnPrevTabAccelerator(null!, null); break;
                case "sidebar": OnToggleSidebarAccelerator(null!, null); break;
                case "help": OnHelpAccelerator(null!, null); break;
            }
        }
        catch (Exception ex) { StatusText.Text = "That shortcut did not work: " + ex.Message; }
    }

    private void OnHistory(object s, RoutedEventArgs e) => OpenPanel("history", BuildHistory, MoreButton, live: false);
    private void OnHistoryAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs? e) { Handle(e); OnHistory(s, new RoutedEventArgs()); }
    private void OnDownloads(object s, RoutedEventArgs e) => OpenPanel("downloads", BuildDownloads, MoreButton);
    private void OnDownloadsAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs? e) { Handle(e); OnDownloads(s, new RoutedEventArgs()); }

    private static string When(DateTimeOffset t)
    {
        var local = t.ToLocalTime(); var today = DateTime.Now.Date;
        return local.Date == today ? $"Today {local:t}" : local.Date == today.AddDays(-1) ? $"Yesterday {local:t}" : local.ToString("d MMM yyyy");
    }

    private (string Title, UIElement Body)? BuildHistory()
    {
        if (_kernel is null || _history is null) return null;
        var repo = _history;
        var box = new TextBox { PlaceholderText = "Search your history" };
        AutomationProperties.SetName(box, "Search your history");
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        var note = new TextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Tokens.Brush("JevTextSecondaryBrush"),
            Text = "Stored on this device. Private sessions, agent sessions and pages classed Sensitive (banking, health) are never recorded.",
        };
        List<HistoryVisit> shown = [];

        void Refresh()
        {
            shown = repo.List(box.Text).ToList();
            list.Items.Clear();
            foreach (var v in shown)
            {
                var row = new Grid { ColumnSpacing = Tokens.Space(8), Padding = Tokens.Inset("JevInsetSlim") };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new StackPanel { Spacing = Tokens.Space(2) };
                var title = string.IsNullOrWhiteSpace(v.Title) ? v.Host : v.Title;
                text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, MaxLines = 2, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                text.Children.Add(new TextBlock { Text = $"{v.Host} · {When(v.VisitedAt)}", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Tokens.Brush("JevTextSecondaryBrush") });
                row.Children.Add(text);
                var remove = new Button { Content = "Forget", Style = (Style)Application.Current.Resources["JevToolButton"] };
                AutomationProperties.SetName(remove, $"Forget {title} from history");
                var url = v.Url;
                remove.Click += (_, _) => { repo.Remove(url); Refresh(); };
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                AutomationProperties.SetName(row, $"{title}. {v.Host}. {When(v.VisitedAt)}");
                list.Items.Add(row);
            }
            if (shown.Count == 0)
                list.Items.Add(new TextBlock { Text = box.Text.Trim().Length > 0 ? "Nothing in your history matches." : "Nothing yet. Pages you visit in ordinary workspaces appear here.", TextWrapping = TextWrapping.Wrap, Foreground = Tokens.Brush("JevTextSecondaryBrush") });
        }

        async Task Open(int index)
        {
            if (index < 0 || index >= shown.Count) return;
            var url = new Uri(shown[index].Url);
            ClosePanel(restoreFocus: false);
            try { var t = _kernel.Open(url); await _kernel.ActivateAsync(t.Id); }
            catch (Exception ex) { StatusText.Text = "Could not open that page: " + ex.Message; }
        }

        var clear = new Button { Content = "Clear all history…", Style = (Style)Application.Current.Resources["JevToolButton"] };
        clear.Click += async (_, _) =>
        {
            var dlg = new ContentDialog
            {
                Title = "Clear all history?", Content = new TextBlock { Text = "This removes the list of pages you visited. It does not sign you out, and it does not delete bookmarks, downloads or Browser Memory.", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Clear history", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot,
            };
            if (await dlg.ShowSerializedAsync() != ContentDialogResult.Primary) return;
            var n = repo.Clear(); Refresh(); StatusText.Text = $"Cleared {n} history entr{(n == 1 ? "y" : "ies")}.";
        };

        box.TextChanged += (_, _) => Refresh();
        list.ItemClick += async (_, ev) => await Open(list.Items.IndexOf(ev.ClickedItem));
        box.KeyDown += async (_, ev) => { if (ev.Key == Windows.System.VirtualKey.Enter && shown.Count > 0) { ev.Handled = true; await Open(0); } };
        Refresh();
        _panelFocus = box;
        return ("History", new StackPanel { Spacing = Tokens.Space(8), Children = { box, list, note, clear } });
    }

    private (string Title, UIElement Body)? BuildDownloads()
    {
        if (_downloads is null) return null;
        var repo = _downloads;
        var list = new ListView { SelectionMode = ListViewSelectionMode.None };
        var rows = _sessionDownloads.Select(d => (d: d.Rec, session: true)).Concat(repo.List().Select(d => (d, session: false))).OrderByDescending(x => x.d.FinishedAt).ToList();
        foreach (var (d, session) in rows)
        {
            var row = new Grid { ColumnSpacing = Tokens.Space(8), Padding = Tokens.Inset("JevInsetSlim") };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = Tokens.Space(2) };
            text.Children.Add(new TextBlock { Text = d.Name, TextWrapping = TextWrapping.Wrap, MaxLines = 2, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var exists = d.Completed && d.Path.Length > 0 && File.Exists(d.Path);
            var state = !d.Completed ? "Did not finish" : exists ? "Saved" : "File is no longer there";
            text.Children.Add(new TextBlock
            {
                Text = $"{state} · {(d.SourceHost.Length > 0 ? d.SourceHost + " · " : "")}{When(d.FinishedAt)}" + (session ? " · Private session, not kept after you close JevBrowse" : ""),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Tokens.Brush("JevTextSecondaryBrush"),
            });
            row.Children.Add(text);
            var show = new Button { Content = "Show in folder", Style = (Style)Application.Current.Resources["JevToolButton"], IsEnabled = exists };
            AutomationProperties.SetName(show, $"Show {d.Name} in its folder");
            var path = d.Path;
            show.Click += (_, _) => ShowInFolder(path);
            Grid.SetColumn(show, 1);
            row.Children.Add(show);
            AutomationProperties.SetName(row, $"{d.Name}. {state}");
            list.Items.Add(row);
        }
        if (rows.Count == 0) list.Items.Add(new TextBlock { Text = "No downloads yet.", Foreground = Tokens.Brush("JevTextSecondaryBrush") });
        var note = new TextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Tokens.Brush("JevTextSecondaryBrush"),
            Text = "Files are never opened from here, only shown in their folder: check a file before you run it. Clearing the list does not delete any file.",
        };
        var clear = new Button { Content = "Clear list", Style = (Style)Application.Current.Resources["JevToolButton"], IsEnabled = rows.Count > 0 };
        clear.Click += (_, _) => { repo.Clear(); _sessionDownloads.Clear(); RefreshPanel(force: true); StatusText.Text = "Cleared the downloads list. The files were not deleted."; };
        return ("Downloads", new StackPanel { Spacing = Tokens.Space(8), Children = { list, note, clear } });
    }

    private void ShowInFolder(string path)
    {
        try
        {
            if (!File.Exists(path)) { StatusText.Text = "That file is no longer there."; return; }
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("/select," + path);
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { StatusText.Text = "Could not open the folder: " + ex.Message; }
    }

    // ---- zoom: the ladder people expect, remembered per site ----

    /// <summary>Zoom of a page is remembered by host, except in Private/agent sessions and on Sensitive pages, where the host itself must not be kept.</summary>
    private bool MayRememberZoom(VirtualTab t) => t.Url.Scheme is "http" or "https" && _kernel!.ClassOf(t) < DataClass.Sensitive;

    // The engine's own page zoom is not exposed by the WinUI web view, so zoom is applied as CSS zoom on the page. It scales text and layout the way browser zoom does
    // but differs on a few pages that size things in viewport units. A tab that may not remember (Private, agent, Sensitive) keeps its zoom in memory only.
    private readonly Dictionary<ResourceId, double> _tabZoom = [];

    private static string ZoomScript(double z) => "document.documentElement&&(document.documentElement.style.zoom='" + z.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "')";

    private double CurrentZoom(VirtualTab tab) =>
        _tabZoom.TryGetValue(tab.Id, out var z) ? z : _siteZoom is not null && MayRememberZoom(tab) ? _siteZoom.Get(tab.Url.Host) : ZoomLevels.Default;

    private void Zoom(int direction, bool reset = false) => WithActiveLease(l =>
    {
        var tab = _kernel!.Active!;
        l.View.Focus(FocusState.Programmatic);
        var next = reset ? ZoomLevels.Default : ZoomLevels.Step(CurrentZoom(tab), direction);
        _tabZoom[tab.Id] = next;
        _ = l.View.CoreWebView2?.ExecuteScriptAsync(ZoomScript(next));
        var remembered = _siteZoom is not null && MayRememberZoom(tab);
        if (remembered) { try { _siteZoom!.Set(tab.Url.Host, next); } catch (Exception) { remembered = false; } }
        StatusText.Text = $"Zoom {ZoomLevels.Label(next)}" + (remembered ? $" on {tab.Url.Host}" : " (this tab only)");
    });

    /// <summary>Puts a freshly loaded page back to the zoom the person chose for its site (or, where nothing may be remembered, for this tab).</summary>
    private void ApplySiteZoom(ResourceId id)
    {
        try
        {
            if (_kernel!.Tabs.FirstOrDefault(t => t.Id == id) is not { } tab || !_leases!.TryGet(id, out var lease)) return;
            var want = MayRememberZoom(tab) && _siteZoom is not null ? _siteZoom.Get(tab.Url.Host) : _tabZoom.GetValueOrDefault(id, ZoomLevels.Default);
            _tabZoom[id] = want;
            if (Math.Abs(want - ZoomLevels.Default) > 0.001) _ = ((WebView2Lease)lease).View.CoreWebView2?.ExecuteScriptAsync(ZoomScript(want));
        }
        catch (Exception) { /* zoom is a convenience; a page must never fail to show because of it */ }
    }
}
