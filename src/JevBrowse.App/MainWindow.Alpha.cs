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
        Show(ExplainButton, power);
        Show(MoveButton, power);
        Show(TimelineButton, power);
        Show(ModeBox, power);
        Show(DevButton, mode == ProductMode.Developer);
        Show(AgentsButton, mode == ProductMode.Agent);
        Show(ReceiptButton, mode != ProductMode.Simple);
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

    private async void OnPalette(object s, RoutedEventArgs e)
    {
        if (_kernel is null) return;
        var commands = BuildCommands();
        var box = new TextBox { PlaceholderText = "Type a command, or text to search browser memory…" };
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360 };
        List<Command> shown = [];
        void Filter()
        {
            var words = box.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            shown = commands.Where(c => words.All(w => c.Text.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(30).ToList();
            if (box.Text.Trim().Length > 1) shown.Add(new Command($"Search browser memory for \"{box.Text.Trim()}\"", () => ShowMemoryAsync(box.Text.Trim())));
            list.Items.Clear();
            foreach (var c in shown) list.Items.Add(c.Text);
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        box.TextChanged += (_, _) => Filter();
        Filter();
        var dlg = new ContentDialog { Title = "Command palette", Content = new StackPanel { Spacing = 8, Children = { box, list } }, PrimaryButtonText = "Run", CloseButtonText = "Close", XamlRoot = Content.XamlRoot, DefaultButton = ContentDialogButton.Primary };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        box.KeyDown += (_, k) => { if (k.Key == Windows.System.VirtualKey.Down && list.SelectedIndex < list.Items.Count - 1) { list.SelectedIndex++; k.Handled = true; } else if (k.Key == Windows.System.VirtualKey.Up && list.SelectedIndex > 0) { list.SelectedIndex--; k.Handled = true; } };
        if (await dlg.ShowSerializedAsync() != ContentDialogResult.Primary || list.SelectedIndex < 0 || list.SelectedIndex >= shown.Count) return;
        try { await shown[list.SelectedIndex].Run(); }
        catch (Exception ex) { StatusText.Text = "command failed: " + ex.Message; }
    }

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
            new("Grant Claude Code localhost + GitHub for 30 minutes", () => GrantAgentAsync(30)),
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

    private async void OnReceipt(object s, RoutedEventArgs e)
    {
        if (_kernel?.Active is not { } t || _shield is null) return;
        _shield.Stats.TryGetValue(t.Id, out var st);
        var sample = ProcessGroupProbe.Sample(_leases!.ProcessIds);
        var live = _kernel.Tabs.Count(x => x.State.HasLiveRenderer());
        var duration = st is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - st.StartedAt;
        var lines = new[]
        {
            $"SESSION RECEIPT — {t.Url.Host}",
            $"Duration                 {duration:h\\:mm\\:ss}                (measured since renderer attached)",
            $"Requests                 {st?.Total ?? 0,-6}                 (measured)",
            $"Third-party requests     {st?.ThirdParty ?? 0,-6}                 (measured, {st?.ThirdPartyHosts.Count ?? 0} hosts)",
            $"Blocked requests         {st?.Blocked ?? 0,-6}                 (measured)",
            $"Transferred              not measured in V1",
            $"Attributed memory        ~{(live == 0 ? 0 : sample.PrivateMb / live):F0} MB              (ESTIMATE: equal share of {sample.PrivateMb:F0} MB across {live} live renderers)",
            $"Data class               {_kernel.ClassOf(t)}",
            $"Container                {_kernel.ContainerOf(t)}",
            $"Protection               {t.Protection}",
            "",
            "Measured values come from the OS or the request pipeline. Estimates are labelled.",
        };
        await new ContentDialog { Title = "Receipt", Content = new TextBlock { Text = string.Join("\n", lines), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, CloseButtonText = "Close", XamlRoot = Content.XamlRoot }.ShowSerializedAsync();
    }
}
