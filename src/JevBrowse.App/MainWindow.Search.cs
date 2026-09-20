using JevBrowse.Domain;
using JevBrowse.Memory;
using JevBrowse.VirtualTabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace JevBrowse.App;

/// <summary>
/// Unified search (P3): one box, in the side panel, over open tabs, workspaces, commands and pages read before. Ctrl+K and
/// the More menu open it. Ranking is <see cref="UnifiedSearch"/>; this file only gathers candidates and runs the choice.
/// </summary>
public sealed partial class MainWindow
{
    private void OnPalette(object s, RoutedEventArgs e) => OpenPanel("search", BuildSearch, MoreButton, live: false);

    private (string Title, UIElement Body)? BuildSearch()
    {
        if (_kernel is null) return null;
        var k = _kernel;
        var box = new TextBox { PlaceholderText = "Search tabs, workspaces, commands, pages you read" };
        AutomationProperties.SetName(box, "Search tabs, workspaces, commands and pages you have read");
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
        var note = new TextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Tokens.Brush("JevTextSecondaryBrush"),
            Text = "Searched on this device only. Private sessions are left out unless you are in one.",
        };
        var actions = new Dictionary<string, Func<Task>>();
        List<SearchResult> shown = [];

        static string KindLabel(SearchKind kind) => kind switch { SearchKind.Tab => "Open tab", SearchKind.Workspace => "Workspace", SearchKind.Command => "Command", _ => "Page you read" };

        List<SearchEntry> Candidates(string q)
        {
            actions.Clear();
            var c = new List<SearchEntry>();
            var active = k.ActiveWorkspace;

            // Private and disposable sessions are ephemeral by promise. Their tabs and workspaces do not show up in search from
            // anywhere else, so a search over-the-shoulder never names them.
            var names = k.Workspaces.ToDictionary(w => w.Id, w => w);
            bool Visible(ContextId ws) => ws == active || !(names.TryGetValue(ws, out var w) && w.Container.IsEphemeral());
            foreach (var t in k.Tabs.Where(t => Visible(t.WorkspaceId)).OrderByDescending(t => t.WorkspaceId == active))
            {
                var key = $"tab:{t.Id}";
                var where = t.WorkspaceId == active ? "" : $" · in {(names.TryGetValue(t.WorkspaceId, out var w) ? w.Name : "another workspace")}";
                var state = t.State.HasLiveRenderer() ? "" : " · asleep";
                c.Add(new SearchEntry(SearchKind.Tab, new TabItem(t).Title, (t.Url.Scheme == "jev" ? "local page" : t.Url.Host) + state + where, key));   // same name the sidebar shows
                var tab = t;
                actions[key] = async () =>
                {
                    if (tab.WorkspaceId != k.ActiveWorkspace) { await k.SwitchWorkspaceAsync(tab.WorkspaceId); RebuildWorkspaces(); }
                    await k.ActivateAsync(tab.Id);
                };
            }
            foreach (var w in k.Workspaces.Where(w => Visible(w.Id) && w.Id != active))
            {
                var id = w.Id; var key = $"ws:{id}";
                c.Add(new SearchEntry(SearchKind.Workspace, $"Switch to workspace: {w.Name}", w.Container.ToString(), key));
                actions[key] = async () => { await k.SwitchWorkspaceAsync(id); RebuildWorkspaces(); };
            }
            var commands = BuildCommands().Where(cmd => !cmd.Text.StartsWith("Switch workspace:", StringComparison.Ordinal)).ToList();
            for (var i = 0; i < commands.Count; i++)
            {
                var key = $"cmd:{i}"; var cmd = commands[i];
                c.Add(new SearchEntry(SearchKind.Command, cmd.Text, "", key));
                actions[key] = cmd.Run;
            }
            // Browser Memory only ever holds pages Trust OS allowed to be indexed, and searches the active workspace only.
            if (q.Length > 1 && _memory is not null)
            {
                var rank = 0;
                foreach (var h in _memory.Search(q, 6, active))
                {
                    var key = $"page:{h.Url}";
                    c.Add(new SearchEntry(SearchKind.Page, h.Title, $"{h.Site} · {h.CapturedAt.ToLocalTime():d MMM} — {h.Snippet}", key, PreMatched: 60 - rank++));
                    var hit = h;
                    actions[key] = async () =>
                    {
                        var existing = k.Tabs.FirstOrDefault(t => t.Url == hit.Url);
                        if (existing is not null) await k.ActivateAsync(existing.Id);
                        else { var t = k.Open(hit.Url); await k.ActivateAsync(t.Id); }
                    };
                }
            }
            return c;
        }

        void Refresh()
        {
            var q = box.Text.Trim();
            shown = UnifiedSearch.Rank(q, Candidates(q)).ToList();
            list.Items.Clear();
            foreach (var r in shown)
            {
                var e = r.Entry;
                var item = new StackPanel { Spacing = Tokens.Space(2), Padding = Tokens.Inset("JevInsetSlim") };
                item.Children.Add(new TextBlock { Text = e.Title, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                var detail = string.IsNullOrEmpty(e.Detail) ? KindLabel(e.Kind) : $"{KindLabel(e.Kind)} · {e.Detail}";
                item.Children.Add(new TextBlock { Text = detail, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Foreground = Tokens.Brush("JevTextSecondaryBrush") });
                AutomationProperties.SetName(item, $"{KindLabel(e.Kind)}: {e.Title}. {e.Detail}");
                list.Items.Add(item);
            }
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            else if (q.Length > 0) list.Items.Add(new TextBlock { Text = "Nothing matches.", Foreground = Tokens.Brush("JevTextSecondaryBrush") });
        }

        async Task Run(int index)
        {
            if (index < 0 || index >= shown.Count || !actions.TryGetValue(shown[index].Entry.Key, out var run)) return;
            // The choice is made; the panel has done its job. Focus is left to whatever the action does (a tab activating puts it in
            // the page), not pulled back to the toolbar.
            ClosePanel(restoreFocus: false);
            try { await run(); }
            catch (Exception ex) { StatusText.Text = "That did not work: " + ex.Message; }
        }

        box.TextChanged += (_, _) => Refresh();
        box.KeyDown += async (_, ev) =>
        {
            switch (ev.Key)
            {
                case Windows.System.VirtualKey.Down when list.SelectedIndex < list.Items.Count - 1: list.SelectedIndex++; ev.Handled = true; break;
                case Windows.System.VirtualKey.Up when list.SelectedIndex > 0: list.SelectedIndex--; ev.Handled = true; break;
                case Windows.System.VirtualKey.Enter: ev.Handled = true; await Run(list.SelectedIndex); break;
            }
        };
        list.ItemClick += async (_, ev) => await Run(list.Items.IndexOf(ev.ClickedItem));
        list.KeyDown += async (_, ev) => { if (ev.Key == Windows.System.VirtualKey.Enter) { ev.Handled = true; await Run(list.SelectedIndex); } };
        Refresh();
        _panelFocus = box;
        return ("Search", new StackPanel { Spacing = Tokens.Space(8), Children = { box, note, list } });
    }
}
