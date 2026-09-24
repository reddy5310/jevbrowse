using JevBrowse.Domain;
using JevBrowse.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace JevBrowse.App;

/// <summary>
/// Bookmarks and the search-engine choice: the two things a daily browser is missed for first. Bookmarks live in the database (a saved address, not history), can be
/// imported from the HTML file every browser exports, and are never written from a Private or Disposable session (those promise to leave nothing).
/// </summary>
public sealed partial class MainWindow
{
    private BookmarkRepository? _bookmarks;

    private void OnBookmarks(object s, RoutedEventArgs e) => OpenPanel("bookmarks", BuildBookmarks, MoreButton, live: false);
    private void OnBookmarkThis(object s, RoutedEventArgs e) => BookmarkCurrentPage();
    private void OnBookmarkAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs? e) { Handle(e); BookmarkCurrentPage(); }
    private void OnBookmarksAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs? e) { Handle(e); OnBookmarks(s, new RoutedEventArgs()); }

    /// <summary>The shared rule for "may this page be bookmarked from here", so the button, the shortcut and the tests agree.</summary>
    private bool TryBookmarkActive(out Bookmark? bookmark, out string message)
    {
        bookmark = null;
        var k = _kernel;
        if (k?.Active is not { } tab || _bookmarks is null) { message = "There is no page to bookmark."; return false; }
        if (tab.Url.Scheme is not ("http" or "https")) { message = "Only web pages can be bookmarked."; return false; }
        if (k.Workspaces.FirstOrDefault(w => w.Id == tab.WorkspaceId)?.Container.IsEphemeral() == true)
        { message = "Private sessions save nothing, so they cannot add bookmarks. Open the page in an ordinary workspace to bookmark it."; return false; }
        bookmark = new Bookmark(tab.Url.AbsoluteUri, new TabItem(tab).Title);
        message = "";
        return true;
    }

    private void BookmarkCurrentPage()
    {
        try
        {
            if (!TryBookmarkActive(out var b, out var why)) { StatusText.Text = why; return; }
            if (_bookmarks!.Contains(b!.Url)) { _bookmarks.Remove(b.Url); StatusText.Text = "Removed the bookmark: " + b.Title; }
            else { _bookmarks.Save(b); StatusText.Text = "Bookmarked: " + b.Title; }
            if (PanelOpen && _panelId == "bookmarks") RefreshPanel(force: true);
        }
        catch (Exception ex) { StatusText.Text = "Could not change the bookmark: " + ex.Message; }
    }

    private (string Title, UIElement Body)? BuildBookmarks()
    {
        if (_kernel is null || _bookmarks is null) return null;
        var repo = _bookmarks;
        var box = new TextBox { PlaceholderText = "Search your bookmarks" };
        AutomationProperties.SetName(box, "Search your bookmarks");
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        var note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Tokens.Brush("JevTextSecondaryBrush"), Text = "Stored on this device only." };
        List<Bookmark> shown = [];

        void Refresh()
        {
            shown = repo.List(box.Text).ToList();
            list.Items.Clear();
            foreach (var b in shown)
            {
                var row = new Grid { ColumnSpacing = Tokens.Space(8), Padding = Tokens.Inset("JevInsetSlim") };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new StackPanel { Spacing = Tokens.Space(2) };
                text.Children.Add(new TextBlock { Text = b.Title, TextWrapping = TextWrapping.Wrap, MaxLines = 2, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                var where = new Uri(b.Url).Host + (b.Folder.Length > 0 ? " · " + b.Folder : "");
                text.Children.Add(new TextBlock { Text = where, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Tokens.Brush("JevTextSecondaryBrush") });
                row.Children.Add(text);
                var remove = new Button { Content = "Remove", Style = (Style)Application.Current.Resources["JevToolButton"] };
                AutomationProperties.SetName(remove, $"Remove bookmark {b.Title}");
                var url = b.Url;
                remove.Click += (_, _) => { repo.Remove(url); Refresh(); };
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                AutomationProperties.SetName(row, $"Bookmark: {b.Title}. {where}");
                list.Items.Add(row);
            }
            if (shown.Count == 0)
                list.Items.Add(new TextBlock
                {
                    Text = box.Text.Trim().Length > 0 ? "No bookmark matches." : "No bookmarks yet. Press Ctrl+D on a page, or import a file exported from another browser.",
                    TextWrapping = TextWrapping.Wrap, Foreground = Tokens.Brush("JevTextSecondaryBrush"),
                });
        }

        async Task Open(int index)
        {
            if (index < 0 || index >= shown.Count) return;
            var url = new Uri(shown[index].Url);
            ClosePanel(restoreFocus: false);
            try { var t = _kernel.Open(url); await _kernel.ActivateAsync(t.Id); }
            catch (Exception ex) { StatusText.Text = "Could not open the bookmark: " + ex.Message; }
        }

        var addThis = new Button { Content = "Bookmark this page", Style = (Style)Application.Current.Resources["JevToolButton"] };
        addThis.Click += (_, _) => { BookmarkCurrentPage(); Refresh(); };
        var import = new Button { Content = "Import bookmarks file…", Style = (Style)Application.Current.Resources["JevToolButton"] };
        import.Click += async (_, _) => { await ImportBookmarksAsync(); Refresh(); };
        var export = new Button { Content = "Export bookmarks file…", Style = (Style)Application.Current.Resources["JevToolButton"] };
        export.Click += async (_, _) => await ExportBookmarksAsync();
        var importHelp = new TextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Tokens.Brush("JevTextSecondaryBrush"),
            Text = "In Chrome, Edge, Firefox or Brave, export your bookmarks as an HTML file from their bookmark manager, then choose it here. Only web addresses are imported; nothing leaves your device.",
        };

        var engine = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Search engine for the address bar" };
        AutomationProperties.SetName(engine, "Search engine for the address bar");
        foreach (var e in SearchEngines.All) engine.Items.Add(e.Name);
        engine.SelectedIndex = Math.Max(0, SearchEngines.All.ToList().FindIndex(e => e.Id == SearchEngines.Find(UiPrefs.Load(DataDir).SearchEngine).Id));
        engine.SelectionChanged += (_, _) =>
        {
            if (engine.SelectedIndex < 0) return;
            var chosen = SearchEngines.All[engine.SelectedIndex];
            UiPrefs.Load(DataDir).WithSearchEngine(chosen.Id).Save(DataDir);
            StatusText.Text = "The address bar now searches with " + chosen.Name + ".";
        };

        box.TextChanged += (_, _) => Refresh();
        list.ItemClick += async (_, ev) => await Open(list.Items.IndexOf(ev.ClickedItem));
        box.KeyDown += async (_, ev) => { if (ev.Key == Windows.System.VirtualKey.Enter && shown.Count > 0) { ev.Handled = true; await Open(0); } };
        Refresh();
        _panelFocus = box;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space(8), Children = { addThis, import, export } };
        return ("Bookmarks", new StackPanel { Spacing = Tokens.Space(8), Children = { box, actions, list, note, importHelp, engine } });
    }

    private async Task ImportBookmarksAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".html"); picker.FileTypeFilter.Add(".htm");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var info = new FileInfo(file.Path);
            if (info.Length > BookmarkImport.MaxFileChars) { StatusText.Text = "That file is too large to be a bookmarks export."; return; }
            var parsed = BookmarkImport.Parse(await File.ReadAllTextAsync(file.Path));
            if (parsed.Count == 0) { StatusText.Text = "No web bookmarks were found in that file. Is it a bookmarks export (HTML)?"; return; }
            var added = _bookmarks!.SaveMany(parsed);
            StatusText.Text = $"Imported {added} new bookmark{(added == 1 ? "" : "s")}" + (added < parsed.Count ? $" ({parsed.Count - added} were already saved)." : ".");
        }
        catch (Exception ex) { StatusText.Text = "Could not import that file: " + ex.Message; }
    }

    private async Task ExportBookmarksAsync()
    {
        try
        {
            var all = _bookmarks!.List(limit: int.MaxValue);
            if (all.Count == 0) { StatusText.Text = "There are no bookmarks to export yet."; return; }
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeChoices.Add("Bookmarks (HTML)", new List<string> { ".html" });
            picker.SuggestedFileName = "jevbrowse-bookmarks";
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await File.WriteAllTextAsync(file.Path, BookmarkExport.ToHtml(all));
            StatusText.Text = $"Exported {all.Count} bookmark{(all.Count == 1 ? "" : "s")} to {file.Name}. Any browser can import this file.";
        }
        catch (Exception ex) { StatusText.Text = "Could not export bookmarks: " + ex.Message; }
    }
}
