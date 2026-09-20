using System.Text.Json;
using JevBrowse.AgentGateway;
using JevBrowse.Diagnostics;
using JevBrowse.Domain;
using JevBrowse.ResourceOS;
using JevBrowse.Shield;
using JevBrowse.Storage;
using JevBrowse.TrustOS;
using JevBrowse.VirtualTabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace JevBrowse.App;

/// <summary>Phase 11 surfaces: product modes (Table A.13), command palette (§26), session receipts (§15).</summary>
public sealed partial class MainWindow
{
    // ---- Product modes ----

    public enum ProductMode { Simple, Focus, Power, Developer, Agent, Private }

    private ProductMode _mode = ProductMode.Power;
    private ProductMode _returnMode = ProductMode.Simple;
    private ContextId _returnWorkspace = ContextId.Default;
    private ContextId? _privateWorkspace;
    private readonly SemaphoreSlim _productModeGate = new(1, 1);
    private bool _syncingProductMode;
    private Task _modeChangeTask = Task.CompletedTask;
    private readonly HashSet<ContextId> _pendingPrivateCleanup = [];
    private bool _privateEndPromptOpen;

    private void ApplyProductMode(ProductMode mode)
    {
        _mode = mode;
        bool power = mode is ProductMode.Power or ProductMode.Developer or ProductMode.Agent;
        bool ai = mode is ProductMode.Focus or ProductMode.Power or ProductMode.Developer or ProductMode.Agent;
        void Show(UIElement e, bool on) => e.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Show(AskButton, ai);
        Show(BrainButton, ai);
        Show(MemoryButton, mode != ProductMode.Simple && mode != ProductMode.Private);
        Show(TimelineButton, power);
        Show(ModeBox, power);
        Show(DevButton, mode == ProductMode.Developer);
        Show(AgentsButton, mode == ProductMode.Agent);
        // Explain, Receipt and the "This tab" menu are always present. The Tools menu appears only when at least one
        // of its items does, so Simple mode does not show a menu that opens onto nothing.
        Show(ToolsButton, mode is not (ProductMode.Simple or ProductMode.Private));
        // Modes are capability switches, not just visibility: what a mode hides it must also stop doing.
        if (_indexer is not null) _indexer.Enabled = mode is not (ProductMode.Simple or ProductMode.Private);
        if (_dev is not null) _dev.SetEnabled(mode == ProductMode.Developer || Environment.GetEnvironmentVariable("JEVBROWSE_DEVSPACE") == "1");
        UpdateEnvChrome();
    }

    private async Task EnsurePrivateWorkspaceAsync()
    {
        if (_kernel is null) return;
        // The container is part of the workspace's identity from birth (it cannot be changed afterwards).
        var ws = _kernel.Workspaces.FirstOrDefault(w => w.Id == _privateWorkspace)
                 ?? _kernel.CreateWorkspace("Private", IdentityContainer.Private);
        _privateWorkspace = ws.Id;
        await _kernel.SwitchWorkspaceAsync(ws.Id);
        RebuildWorkspaces();
        UpdateIdlePanel();
    }

    private async void OnProductModeChanged(object s, SelectionChangedEventArgs e)
    {
        if (_syncingProductMode || ProductModeBox.SelectedIndex < 0 || _kernel is null) return;
        _modeChangeTask = ChangeProductModeAsync((ProductMode)ProductModeBox.SelectedIndex);
        try { await _modeChangeTask; }
        catch (Exception) { StatusText.Text = "Could not switch browsing mode. Please try again."; }
    }

    private async Task ChangeProductModeAsync(ProductMode mode)
    {
        await _productModeGate.WaitAsync();
        try
        {
            if (_kernel is null) return;
            if (mode == ProductMode.Private)
            {
                if (_mode != ProductMode.Private)
                {
                    _returnMode = _mode;
                    _returnWorkspace = _kernel.ActiveWorkspace;
                }
                await EnsurePrivateWorkspaceAsync();
            }
            else if (_mode == ProductMode.Private)
                await _kernel.SwitchWorkspaceAsync(ReturnWorkspace());
            ApplyProductMode(mode);
            UpdatePrivateSessionUi();
            StatusText.Text = mode == ProductMode.Private
                ? "Private session open. Return keeps it open in the background; End private session closes it."
                : $"mode: {mode}";
        }
        finally { _productModeGate.Release(); }
    }

    private ContextId ReturnWorkspace() => _kernel!.Workspaces.Any(w => w.Id == _returnWorkspace && !w.Container.IsEphemeral())
        ? _returnWorkspace : _kernel.Workspaces.First(w => !w.Container.IsEphemeral()).Id;

    private void UpdatePrivateSessionUi()
    {
        if (_kernel is null) return;

        // Where "Return" leads. Recorded whenever the user is in an ordinary workspace, so entering Private by ANY
        // route (the mode box, the workspace box) still returns them to where they actually were. Ephemeral
        // workspaces are never a place to return to, and an agent's must not overwrite it.
        var current = _kernel.Workspaces.FirstOrDefault(w => w.Id == _kernel.ActiveWorkspace);
        if (current is not null && !current.Container.IsEphemeral()) _returnWorkspace = current.Id;

        var tabs = _privateWorkspace is { } pw ? _kernel.TabsIn(pw).Count() : 0;
        var view = PrivateSessionPresentation.Describe(_kernel.Workspaces, _kernel.ActiveWorkspace, _privateWorkspace,
            ReturnWorkspace(), tabs, _pendingPrivateCleanup.Count);

        EndPrivateButton.Visibility = view.ShowEnd ? Visibility.Visible : Visibility.Collapsed;
        EndPrivateButton.Content = view.EndLabel;
        ReturnPrivateButton.Visibility = view.ShowReturn ? Visibility.Visible : Visibility.Collapsed;
        ReturnPrivateButton.Content = view.ReturnLabel;
        PrivateSessionText.Visibility = view.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PrivateSessionText.Text = view.Text;
        AutomationProperties.SetName(EndPrivateButton, view.HasSession ? "End private session and close its tabs" : "Retry private cleanup");

        // The mode box follows the workspace the user is really in. Only THEIR session counts: an agent's Private
        // workspace becoming active must not flip the user's browser into Private mode.
        if (view.InSession && _mode != ProductMode.Private) { _returnMode = _mode; ApplyProductMode(ProductMode.Private); }
        else if (!view.InSession && _mode == ProductMode.Private) ApplyProductMode(_returnMode);
        _syncingProductMode = true;
        ProductModeBox.SelectedIndex = (int)_mode;
        _syncingProductMode = false;
    }

    /// <summary>Leave the private session WITHOUT ending it: changes what is visible, closes nothing.</summary>
    private async void OnReturnFromPrivate(object s, RoutedEventArgs e)
    {
        if (_kernel is null || _shutdownInProgress) return;
        var name = _kernel.Workspaces.FirstOrDefault(w => w.Id == ReturnWorkspace())?.Name ?? "your workspace";
        _modeChangeTask = ChangeProductModeAsync(_returnMode == ProductMode.Private ? ProductMode.Simple : _returnMode);
        try
        {
            await _modeChangeTask;
            StatusText.Text = $"Back in {name}. Your private session is still open; End private session closes it.";
        }
        catch (Exception) { StatusText.Text = "Could not switch back. Please try again."; }
    }

    private async Task<bool> EndPrivateSessionCoreAsync(ContextId workspace)
    {
        _pendingPrivateCleanup.Add(workspace);
        if (_privateWorkspace == workspace) _privateWorkspace = null;
        _permissions?.EndSession(IdentityContainer.Private, workspace);
        await _kernel!.EndPrivateSessionAsync(workspace, ReturnWorkspace());
        var result = await _leases!.EndPrivateSessionAsync(workspace);
        if (result.RenderersClosed && result.ProfileDataDeleted) _pendingPrivateCleanup.Remove(workspace);
        StatusText.Text = !result.RenderersClosed ? "Private tabs are still closing. Retry cleanup."
            : result.ProfileDataDeleted ? "Private tabs closed. Temporary profile data deleted."
            : "Private tabs closed. Temporary profile data is still waiting for deletion. Retry cleanup.";
        return result.RenderersClosed && result.ProfileDataDeleted;
    }

    private async void OnEndPrivateSession(object s, RoutedEventArgs e)
    {
        if (_kernel is null || _privateEndPromptOpen || _shutdownInProgress) return;
        _privateEndPromptOpen = true;
        var gateHeld = false;
        EndPrivateButton.IsEnabled = false;
        try
        {
            // The user's session, by identity. Not "whichever Private workspace is active": that could be an agent's.
            var mine = _privateWorkspace is { } pw ? _kernel.Workspaces.FirstOrDefault(w => w.Id == pw) : null;
            if (mine is not null)
            {
                var n = _kernel.TabsIn(mine.Id).Count();
                var dialog = new ContentDialog
                {
                    Title = "End private session?",
                    Content = $"This closes {(n == 1 ? "its 1 tab" : $"its {n} tabs")}, including calls, downloads and unsaved forms, "
                            + "and deletes the session's cookies and other temporary data once its files are released. "
                            + "Your other tabs are not affected.",
                    PrimaryButtonText = "End session", CloseButtonText = "Keep session open", XamlRoot = Content.XamlRoot,
                };
                if (await dialog.ShowSerializedAsync() != ContentDialogResult.Primary) return;
            }
            if (_shutdownInProgress) return;
            // Never hold this gate while waiting for a dialog: closing the window also needs it for cleanup.
            await _productModeGate.WaitAsync();
            gateHeld = true;
            var wasInside = mine is not null && _kernel.ActiveWorkspace == mine.Id;
            var pendingBeforeEnd = _pendingPrivateCleanup.ToArray();
            if (mine is not null) await EndPrivateSessionCoreAsync(mine.Id);
            foreach (var pending in pendingBeforeEnd.Where(id => id != mine?.Id)) await EndPrivateSessionCoreAsync(pending);
            var status = StatusText.Text;
            // Only move the user if they were inside the session being ended. Ending it from another workspace must
            // leave them exactly where they are.
            if (wasInside)
            {
                await _kernel.SwitchWorkspaceAsync(ReturnWorkspace());
                if (_kernel.Active is null && _kernel.TabsIn(_kernel.ActiveWorkspace).LastOrDefault() is { } tab)
                    await _kernel.ActivateAsync(tab.Id);
            }
            // Moving workspaces rewrites the status line; put back the one EndPrivateSessionCoreAsync composed. It
            // says "deleted" only when the tabs are closed AND the profile data is gone, and offers a retry otherwise.
            StatusText.Text = status;
        }
        catch (Exception) { StatusText.Text = "Private cleanup is incomplete. Retry cleanup to finish closing tabs and deleting temporary data."; }
        finally
        {
            _privateEndPromptOpen = false;
            EndPrivateButton.IsEnabled = true;
            RebuildWorkspaces();
            UpdatePrivateSessionUi();
            if (gateHeld) _productModeGate.Release();
        }
    }

    // ---- Conventional browser shortcuts ----

    private readonly Stack<Uri> _closedTabs = new();

    private static void Handle(KeyboardAcceleratorInvokedEventArgs e) => e.Handled = true;

    private void OnFocusAddress(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Handle(e); AddressBox.Focus(FocusState.Keyboard); AddressBox.SelectAll(); }
    private void OnNewTabAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Handle(e); OnNewTab(s, new RoutedEventArgs()); }
    // What the toolbar decides is measured, never a fixed width: at a larger Windows text size every button is wider, so a
    // threshold that was right at 100% clips at 150%. The decisions read the buttons' own desired widths.
    private const double UrlRoom = 160;   // the least the address text may be given beside its badge
    private bool _addressWrapped;
    private string _badgeFullText = "PERSONAL";

    private static double NaturalWidth(UIElement e)
    {
        if (e.Visibility == Visibility.Collapsed) return 0;
        e.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return e.DesiredSize.Width;
    }

    private void OnToolbarSizeChanged(object s, SizeChangedEventArgs e) => LayoutToolbar();

    /// <summary>
    /// Three rules, in order. The trust and tab controls sit beside the address bar only if everything fits at once; otherwise
    /// they wrap onto rows of their own (a wrapping panel, so nothing is behind a scroll bar and nothing is squeezed). If even
    /// the navigation buttons plus a readable address bar do not fit, the address bar gets a full row and its badge shortens to
    /// the profile name (the full state stays in the accessible name and tooltip). Nothing is hidden or moved into a menu.
    /// </summary>
    private void LayoutToolbar()
    {
        var w = Toolbar.ActualWidth;
        if (w <= 0) return;
        var gap = Tokens.Space(6);
        var nav = Toolbar.Children.OfType<Button>().Where(b => Grid.GetColumn(b) <= 3).ToList();
        var navW = nav.Sum(NaturalWidth) + gap * nav.Count;
        var badge = new TextBlock { Text = _badgeFullText, FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        badge.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var pillExtras = Tokens.Inset("JevInsetBadge").Left + Tokens.Inset("JevInsetBadge").Right + Tokens.Inset("JevInsetSlim").Left + Tokens.Inset("JevInsetSlim").Right + Tokens.Space(8) + 4;
        var addressW = badge.DesiredSize.Width + pillExtras + UrlRoom;
        var trustW = NaturalWidth(TrustBar);

        var addressInline = w >= navW + addressW;
        var trustInline = addressInline && w >= navW + addressW + gap + trustW;
        var changed = addressInline == _addressWrapped;   // _addressWrapped is the inverse of addressInline
        _addressWrapped = !addressInline;

        AddressColumn.MinWidth = 0;
        Grid.SetRow(AddressPill, addressInline ? 0 : 1);
        Grid.SetColumn(AddressPill, addressInline ? 4 : 0);
        Grid.SetColumnSpan(AddressPill, addressInline ? 1 : 6);
        Grid.SetRow(TrustBar, trustInline ? 0 : addressInline ? 1 : 2);
        Grid.SetColumn(TrustBar, trustInline ? 5 : 0);
        Grid.SetColumnSpan(TrustBar, trustInline ? 1 : 6);
        if (changed) UpdateClassBadge();
    }

    // ---- Sidebar ----

    private bool _sidebarCollapsed;

    /// <summary>
    /// The sidebar is 276 px of a window that may be 700 wide; hiding it gives that back to the page. Remembered, so a
    /// person who works with it hidden is not made to hide it again every launch. Ctrl+B and the toolbar button both
    /// reach it, and the button's name says what the next press does.
    /// </summary>
    private void ApplySidebar(bool collapsed, bool remember)
    {
        _sidebarCollapsed = collapsed;
        void Layout()
        {
            SidebarColumn.Width = new GridLength(collapsed ? 0 : 276);
            Sidebar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            MainArea.Margin = collapsed ? Tokens.Inset("JevInsetMainFull") : Tokens.Inset("JevInsetMain");
        }
        // Only a person's own toggle animates (remember == true); the state restored at start-up just is. Hiding fades the
        // panel out and then gives its room back; showing gives the room back and fades the panel in.
        if (!remember || !Motion.Enabled) { Sidebar.Opacity = 1; Layout(); }
        else if (collapsed) Motion.Fade(Sidebar, 0f, JevBrowse.VirtualTabs.MotionKind.Fast, () => { Layout(); Sidebar.Opacity = 1; });
        else { Sidebar.Opacity = 0; Layout(); Motion.Fade(Sidebar, 1f, JevBrowse.VirtualTabs.MotionKind.Base); }
        var label = collapsed ? "Show sidebar" : "Hide sidebar";
        ToolTipService.SetToolTip(SidebarToggle, $"{label} (Ctrl+B)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SidebarToggle, label);
        if (!remember) return;
        UiPrefs.Load(DataDir).With(collapsed).Save(DataDir);
    }

    private static bool LoadSidebarCollapsed() => UiPrefs.Load(DataDir).SidebarCollapsed;

    private void OnToggleSidebar(object s, RoutedEventArgs e) => ApplySidebar(!_sidebarCollapsed, remember: true);

    private void OnToggleSidebarAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        Handle(e);
        ApplySidebar(!_sidebarCollapsed, remember: true);
    }

    private void OnReloadClick(object s, RoutedEventArgs e) => WithActiveLease(l => l.View.CoreWebView2?.Reload());

    private void OnReloadAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Handle(e); WithActiveLease(l => l.View.CoreWebView2?.Reload()); }

    private async void OnCloseTabAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        Handle(e);
        if (_kernel?.Active is not { } t) return;
        // Remember it for Ctrl+Shift+T, except where the identity promises no trace (Private/Disposable).
        if (!_kernel.ContainerOf(t).IsEphemeral() && t.Url.Scheme is "http" or "https") { _closedTabs.Push(t.Url); if (_closedTabs.Count > 20) { var keep = _closedTabs.Take(20).Reverse().ToList(); _closedTabs.Clear(); foreach (var u in keep) _closedTabs.Push(u); } }
        await _kernel.CloseAsync(t.Id);
        var next = _kernel.TabsIn(_kernel.ActiveWorkspace).LastOrDefault();
        if (next is not null) await _kernel.ActivateAsync(next.Id);
    }

    private async void OnReopenClosedAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        Handle(e);
        if (_kernel is null || !_closedTabs.TryPop(out var url)) { StatusText.Text = "no recently closed tab"; return; }
        var t = _kernel.Open(url);
        await _kernel.ActivateAsync(t.Id);
    }

    private async Task StepTabAsync(int delta)
    {
        if (_kernel is null) return;
        var tabs = _kernel.TabsIn(_kernel.ActiveWorkspace).ToList();
        if (tabs.Count < 2) return;
        var i = Math.Max(0, tabs.FindIndex(t => t.Id == _kernel.Active?.Id));
        await _kernel.ActivateAsync(tabs[(i + delta + tabs.Count) % tabs.Count].Id);
    }

    private async void OnNextTabAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Handle(e); await StepTabAsync(+1); }
    private async void OnPrevTabAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Handle(e); await StepTabAsync(-1); }

    private void Zoom(double delta, bool reset = false) => WithActiveLease(l =>
    {
        var v = l.View; v.Focus(FocusState.Programmatic);
        _ = v.CoreWebView2?.ExecuteScriptAsync(reset ? "document.body.style.zoom='1'" : $"document.body.style.zoom=String(Math.max(.25, Math.min(5, (parseFloat(document.body.style.zoom||'1')) + ({delta.ToString(System.Globalization.CultureInfo.InvariantCulture)}))))");
    });
    private void OnZoomInAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Handle(e); Zoom(0.1); }
    private void OnZoomOutAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Handle(e); Zoom(-0.1); }
    private void OnZoomResetAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Handle(e); Zoom(0, reset: true); }

    // ---- Command palette (§26) ----

    private sealed record Command(string Text, Func<Task> Run);

    private void OnPaletteAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; OnPalette(s, new RoutedEventArgs()); }

    private List<Command> BuildCommands()
    {
        var k = _kernel!;
        var cmds = new List<Command>
        {
            new("Hibernate everything except current", () => { OnHibernateAll(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("Hibernate this tab", () => { OnHibernateCurrent(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("Show resources using the most memory", ShowMemoryUsageAsync),
            new("Restore an earlier context (Time Travel)", () => { OnTimeline(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("Explain why this resource was virtualized / scheduled", () => { OnExplain(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("Pin: never hibernate this tab", () => { OnPinCurrent(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("Block notifications on this domain", BlockNotificationsAsync),
            new("Open this page in a disposable identity", OpenInDisposableAsync),
            new("Show every third party contacted by this page", ShowThirdPartiesAsync),
            new("Session receipt for this site", () => { OnReceipt(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("Shield: toggle for this site", () => { OnShield(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("Data class for this site…", () => { OnClassBadgeTapped(this, new TappedRoutedEventArgs()); return Task.CompletedTask; }),
            new("Theme: dark", () => { ApplyTheme(ThemePreference.Dark, remember: true); return Task.CompletedTask; }),
            new("Theme: light", () => { ApplyTheme(ThemePreference.Light, remember: true); return Task.CompletedTask; }),
            new("Theme: match Windows", () => { ApplyTheme(ThemePreference.System, remember: true); return Task.CompletedTask; }),
            new("Grant Claude Code localhost + GitHub for 30 minutes", () => GrantAgentAsync(30)),
            new("Workspaces overview: what is in each workspace", () => { OnWorkspaceOverview(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("Agent activity: what agents are doing", () => { OnAgentActivity(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("New workspace…", () => { OnNewWorkspace(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("Update Shield filter lists", UpdateFilterListsAsync),
        };
        foreach (var w in k.Workspaces)
        {
            var id = w.Id;
            cmds.Add(new($"Switch workspace: {w.Name}", async () => { await k.SwitchWorkspaceAsync(id); RebuildWorkspaces(); }));
            if (k.Active is { } a && a.WorkspaceId != w.Id) cmds.Add(new($"Move this tab to: {w.Name}", async () => { await MoveActiveAsync(a, id, w.Name); }));
        }
        foreach (var m in Enum.GetValues<MemoryMode>()) cmds.Add(new($"Memory mode: {m}", () => { ModeBox.SelectedIndex = (int)m; return Task.CompletedTask; }));
        foreach (var m in Enum.GetValues<ProductMode>()) cmds.Add(new($"Product mode: {m}", () => { ProductModeBox.SelectedIndex = (int)m; return Task.CompletedTask; }));
        return cmds;
    }

    /// <summary>
    /// Move a tab. Within one identity it just moves; across identities the kernel opens a NEW tab in the destination
    /// (fresh cookies/storage) and closes the original, and we say so plainly.
    /// </summary>
    private async Task MoveActiveAsync(VirtualTab tab, ContextId dest, string destName)
    {
        var from = _kernel!.ContainerOf(tab);
        var moved = await _kernel.MoveToWorkspaceAsync(tab.Id, dest);
        RebuildWorkspaces();
        var crossed = moved.Id != tab.Id;
        StatusText.Text = crossed
            ? $"opened in '{destName}' as a new tab ({_kernel.ContainerOf(moved)} identity: none of the {from} cookies or logins come along); the original was closed"
            : $"moved to {destName}";
    }

    private async Task ShowMemoryUsageAsync()
    {
        var k = _kernel!;
        var sample = ProcessGroupProbe.Sample(_leases!.ProcessIds);
        var live = k.Tabs.Where(t => t.State.HasLiveRenderer()).ToList();
        var lines = new List<string> { $"Process group: {sample.ProcessCount} processes, {sample.PrivateMb:F0} MB private (measured)", $"{live.Count} live renderers of {k.Tabs.Count} tabs", "", "Live tabs (per-tab attribution is an estimate: equal share of the measured total):" };
        var share = live.Count == 0 ? 0 : sample.PrivateMb / live.Count;
        foreach (var t in live.OrderBy(t => t.State)) lines.Add($"  ~{share:F0} MB  {t.State,-9} {(string.IsNullOrEmpty(t.Title) ? t.Url.Host : t.Title)}");
        var dlg = new ContentDialog { Title = "Memory", Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = string.Join("\n", lines), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap } }, CloseButtonText = "Close", XamlRoot = Content.XamlRoot };
        await dlg.ShowSerializedAsync();
    }

    private Task BlockNotificationsAsync()
    {
        if (_kernel?.Active is not { } t || _permissions is null) return Task.CompletedTask;
        var persisted = _permissions.Block(_kernel.ContainerOf(t), t.WorkspaceId, t.Url, PermissionKind.Notifications);
        StatusText.Text = $"notifications blocked for {t.Url.Scheme}://{t.Url.Host}{(t.Url.IsDefaultPort ? "" : ":" + t.Url.Port)} in {_kernel.ContainerOf(t)}" + (persisted ? "" : " (this private session only; nothing is stored)");
        return Task.CompletedTask;
    }

    private async Task OpenInDisposableAsync()
    {
        if (_kernel?.Active is not { } t) return;
        // A fresh disposable workspace each time: its own profile, gone at shutdown, never persisted.
        var ws = _kernel.CreateWorkspace($"Disposable {DateTime.Now:HH:mm}", IdentityContainer.Disposable);
        await _kernel.SwitchWorkspaceAsync(ws.Id);
        var nt = _kernel.Open(t.Url);
        await _kernel.ActivateAsync(nt.Id);
        RebuildWorkspaces();
    }

    private async Task ShowThirdPartiesAsync()
    {
        if (_kernel?.Active is not { } t || _shield is null) return;
        _shield.Stats.TryGetValue(t.Id, out var st);
        var hosts = st?.ThirdPartyHosts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value,4}  {kv.Key}").ToList() ?? [];
        var text = hosts.Count == 0 ? "No third-party requests seen on this page yet." : $"{hosts.Count} third-party hosts ({st!.ThirdParty} requests, {st.Blocked} blocked):\n\n" + string.Join("\n", hosts);
        await new ContentDialog { Title = $"Third parties: {t.Url.Host}", Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = text, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap } }, CloseButtonText = "Close", XamlRoot = Content.XamlRoot }.ShowSerializedAsync();
    }

    /// <summary>§26 last example. Opens a scoped session from docs/agents/claude-code.json (or the default) and shows the endpoint.</summary>
    private async Task GrantAgentAsync(int minutes)
    {
        if (_agents is null) return;
        AgentManifest manifest;
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "agents", "claude-code.json");
        try { manifest = File.Exists(path) ? JsonSerializer.Deserialize<AgentManifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })! : new AgentManifest(); }
        catch (Exception) { manifest = new AgentManifest(); }
        manifest.Agent = "Claude Code";
        manifest.AllowDomains = ["localhost", "127.0.0.1", "github.com"];
        manifest.SessionMinutes = minutes;
        // The user chose this palette command, which is the approval. The ceiling is exactly this grant.
        if (_agentHost is null || !_agentHost.IsRunning)
        {
            var ceiling = new AgentCeiling { Limits = new AgentManifest { Agent = "ceiling", AllowDomains = [.. manifest.AllowDomains], Actions = [.. manifest.Actions], SessionMinutes = minutes, MaxLivePages = manifest.MaxLivePages, MaxActions = manifest.MaxActions, DestructiveActions = "confirm", Container = IdentityContainer.Disposable } };
            _agentHost = new LocalAgentHost(_agents, ceiling, ApproveAgentSessionAsync);
            _agentHost.Start();
        }
        var (s, _) = await _agentHost.GrantAsync(manifest);   // registered with the host, so the HTTP routes below can find it
        var text = $"Session {s.Id} for {manifest.Agent}: {string.Join(", ", s.Manifest.AllowDomains)} until {s.ExpiresAt.ToLocalTime():HH:mm}.\n\n" +
                   $"Base URL: http://127.0.0.1:{_agentHost.Port}/\nToken:    {_agentHost.Token}\n\n" +
                   $"Example:\ncurl -H \"Authorization: Bearer {_agentHost.Token}\" -X POST http://127.0.0.1:{_agentHost.Port}/sessions/{s.Id}/actions -d '{{\"action\":\"Navigate\",\"url\":\"https://github.com/reddy5310/jevbrowse\"}}'";
        await new ContentDialog { Title = "Agent grant", Content = new TextBlock { Text = text, IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap }, CloseButtonText = "Close", XamlRoot = Content.XamlRoot }.ShowSerializedAsync();
    }

    // ---- Session receipt (§15) ----

    private void OnReceipt(object s, RoutedEventArgs e) => OpenPanel("receipt", BuildReceipt, ReceiptButton);

    /// <summary>What this site did during the visit, as a panel. Rebuilt when the active tab changes, so it never describes a page that is no longer showing.</summary>
    private (string Title, UIElement Body)? BuildReceipt()
    {
        if (_kernel?.Active is not { } t || _shield is null) return null;
        _shield.Stats.TryGetValue(t.Id, out var st);
        var sample = ProcessGroupProbe.Sample(_leases!.ProcessIds);
        var cls = _kernel.ClassOf(t);
        var facts = new ReceiptFacts(
            t.Url.Scheme == "jev" ? "this page" : t.Url.Host,
            st is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - st.StartedAt,
            st?.Total ?? 0, st?.ThirdParty ?? 0, st?.ThirdPartyHosts.Count ?? 0, st?.Blocked ?? 0,
            sample.PrivateMb, _kernel.Tabs.Count(x => x.State.HasLiveRenderer()),
            ClassLabel(cls), ClassExplanation(cls), _kernel.ContainerOf(t).ToString(),
            ProtectionPhrases.StayAwake(t.Protection));

        // Label over value, stacked: reads in order with a screen reader and cannot wrap into misaligned columns.
        var body = new StackPanel { Spacing = Tokens.Space(10) };
        foreach (var (label, value) in ReceiptRows.Build(facts))
        {
            body.Children.Add(new StackPanel
            {
                Spacing = Tokens.Space(2),
                Children =
                {
                    new TextBlock { Text = label, FontSize = 12, Foreground = Tokens.Brush("JevTextSecondaryBrush") },
                    new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                },
            });
        }
        body.Children.Add(new TextBlock { Text = ReceiptRows.Footnote, FontSize = 12, Foreground = Tokens.Brush("JevTextSecondaryBrush"), TextWrapping = TextWrapping.Wrap, Margin = Tokens.Inset("JevInsetNote") });
        return (ReceiptRows.Title(facts), body);
    }
}
