using System.Diagnostics;
using JevBrowse.Domain;
using JevBrowse.Renderer.Abstractions;
using JevBrowse.ResourceOS;
using JevBrowse.Storage;
using JevBrowse.TrustOS;

namespace JevBrowse.VirtualTabs;

/// <summary>
/// <paramref name="Background"/> marks an event about work done on the person's behalf that they are not looking at (an agent's page
/// coming live). The interface must not present those as if they were about the page in front of them: no restore panel, no restore
/// timer, no "woke a tab" message.
/// </summary>
public sealed record KernelEvent(string Kind, ResourceId Id, string Reason, bool Background = false);

/// <summary>
/// Virtual Tab Kernel (Architecture Â§5): owns durable tab identity, ordering, lifecycle, checkpoints and renderer leases.
///
/// Concurrency: every operation that changes renderer ownership or lifecycle state (activate, virtualize, close,
/// workspace switch, plan application, restore, checkpoint-all) runs through one gate, so acquisitions cannot
/// duplicate and a tab cannot be disposed while another operation is using it. Public methods take the gate;
/// private *Core methods assume it is held (no re-entrancy).
///
/// Privacy: nothing is captured or written before Trust OS has cleared it (see AllowThumbnails / EnforcePolicy),
/// and stricter classification retroactively removes what the previous class had allowed.
/// </summary>
public sealed class TabKernel
{
    private readonly List<VirtualTab> _tabs = [];
    private readonly Dictionary<ResourceId, DateTimeOffset> _lastActive = [];
    private readonly Dictionary<ResourceId, Stopwatch> _restoreTimers = [];
    private readonly Dictionary<ResourceId, int> _visits = [];
    private readonly Dictionary<ResourceId, ScheduledAction> _lastDecision = [];
    private readonly IRendererLeaseManager _leases;
    private readonly TabRepository _repo;
    private readonly CheckpointRepository _checkpoints;
    private readonly WorkspaceRepository? _workspaces;
    private readonly List<Workspace> _workspaceList = [];
    private readonly string _thumbnailDir;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ITrustPolicy _trust;
    private readonly DataClassifier _classifier;
    private readonly Dictionary<ResourceId, PageSignals> _signals = [];
    private readonly Dictionary<ResourceId, DataClass> _advisory = [];
    private readonly Dictionary<ResourceId, CaptureResult> _lastCapture = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<ContextId> _endedPrivateSessions = [];

    /// <summary>Marks a "restored" event for a tab that had nothing saved, i.e. a first load rather than a wake-up.</summary>
    public const string FirstLoadSuffix = " (first load)";

    private void RequireOpenWorkspace(ContextId id)
    {
        if (_endedPrivateSessions.Contains(id)) throw new InvalidOperationException("This private session has ended.");
        if (_workspaceList.All(w => w.Id != id)) throw new KeyNotFoundException($"workspace {id}");
    }

    public TabKernel(IRendererLeaseManager leases, TabRepository repo, CheckpointRepository checkpoints, string thumbnailDir,
        Func<DateTimeOffset>? clock = null, WorkspaceRepository? workspaces = null, ITrustPolicy? trust = null, DataClassifier? classifier = null)
    {
        _leases = leases;
        _repo = repo;
        _checkpoints = checkpoints;
        _workspaces = workspaces;
        _thumbnailDir = thumbnailDir;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _trust = trust ?? new DefaultTrustPolicy();
        _classifier = classifier ?? new DataClassifier();
    }

    /// <summary>
    /// Foreground admission prefers to evict tabs that have been live at least this long, so a fast tab-switcher
    /// cannot thrash a tab that was just restored (same intent as the scheduler's MinResidency). If every candidate
    /// is younger the budget still wins and the oldest-active is evicted.
    /// </summary>
    public TimeSpan AdmissionMinResidency { get; set; } = TimeSpan.FromSeconds(30);

    private async Task<T> SerializedAsync<T>(Func<Task<T>> body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await body(); }
        finally { _gate.Release(); }
    }

    private async Task SerializedAsync(Func<Task> body, CancellationToken ct) =>
        await SerializedAsync<bool>(async () => { await body(); return true; }, ct);

    // ---- Trust OS ----

    public IdentityContainer ContainerOf(VirtualTab t) =>
        _workspaceList.FirstOrDefault(w => w.Id == t.WorkspaceId)?.Container ?? IdentityContainer.Personal;

    /// <summary>Effective class: deterministic classification, raised (never lowered) by an advisory from JevBrain layer 4.</summary>
    public DataClass ClassOf(VirtualTab t)
    {
        var c = _classifier.Classify(t.Url, ContainerOf(t), _signals.GetValueOrDefault(t.Id));
        return _advisory.TryGetValue(t.Id, out var adv) && adv > c && c != DataClass.Ephemeral ? adv : c;
    }

    /// <summary>Advisory raise from a probabilistic source. Ignored if it would lower the class (Constitution rule 9).</summary>
    public bool RaiseClass(ResourceId id, DataClass advisory)
    {
        var t = Find(id);
        if (advisory <= ClassOf(t)) return false;
        _advisory[id] = advisory;
        EnforcePolicy(t);
        Changed?.Invoke(new("signals", id, $"advisory:{advisory}"));
        return true;
    }

    /// <summary>
    /// Sensitivity of the page content itself, ignoring the container. An ephemeral container changes what we
    /// persist, not whether a banking page is a banking page; agents and AI ceilings use this.
    /// </summary>
    public DataClass ContentClassOf(VirtualTab t) => _classifier.Classify(t.Url, IdentityContainer.Personal, _signals.GetValueOrDefault(t.Id));

    public PageSignals SignalsOf(VirtualTab t) => _signals.GetValueOrDefault(t.Id);

    /// <summary>Every persistence decision goes through here. Nothing else in the kernel writes to disk directly.</summary>
    public TrustDecision May(VirtualTab t, DataOperation op) =>
        _trust.Evaluate(new ResourceContext(t.Url, ClassOf(t), ContainerOf(t)), op);

    private string ThumbPath(ResourceId id) => Path.Combine(_thumbnailDir, $"{id}.png");

    /// <summary>
    /// Re-applies the current class to everything already stored or in flight: leases stop capturing, and artifacts
    /// the new class no longer allows are deleted (thumbnail file, checkpoint, durable row), and listeners are told
    /// to forget derived data (search index). Called whenever the class can have changed.
    /// </summary>
    private void EnforcePolicy(VirtualTab t)
    {
        var thumbs = May(t, DataOperation.PersistThumbnail).Allowed;
        if (_leases.TryGet(t.Id, out var lease)) lease.AllowThumbnails = thumbs;
        if (!thumbs) { DeleteThumb(ThumbPath(t.Id)); DeleteThumb(ThumbPath(t.Id) + ".tmp"); }
        if (!May(t, DataOperation.PersistCheckpoint).Allowed) _checkpoints.Delete(t.Id);
        if (!May(t, DataOperation.PersistTabRow).Allowed) _repo.Delete(t.Id);
        if (!May(t, DataOperation.IndexContent).Allowed) Changed?.Invoke(new("policy-tightened", t.Id, "index"));
    }

    private void Hide(VirtualTab t)
    {
        if (!_leases.TryGet(t.Id, out var lease)) return;
        lease.AllowThumbnails = May(t, DataOperation.PersistThumbnail).Allowed; // decided BEFORE the deactivation capture
        lease.SetVisible(false);
    }

    public IReadOnlyList<VirtualTab> Tabs => _tabs;
    public VirtualTab? Active { get; private set; }

    // ---- Context OS ----

    public IReadOnlyList<Workspace> Workspaces => _workspaceList;
    public ContextId ActiveWorkspace { get; private set; } = ContextId.Default;
    public IEnumerable<VirtualTab> TabsIn(ContextId ws) => _tabs.Where(t => t.WorkspaceId == ws);

    /// <summary>
    /// The container is fixed at creation. Ephemeral workspaces exist only in memory: no workspace row, no
    /// timeline, so a Private or agent workspace leaves no name behind either.
    /// </summary>
    public Workspace CreateWorkspace(string name, IdentityContainer container = IdentityContainer.Personal)
    {
        var w = new Workspace(ContextId.New(), name) { CreatedAt = _clock(), Container = container };
        _workspaceList.Add(w);
        if (!container.IsEphemeral()) _workspaces?.Upsert(w);
        Changed?.Invoke(new("workspace-created", default, name));
        return w;
    }

    private bool SameIdentity(Workspace a, Workspace b) =>
        a.Container == b.Container && (!a.Container.IsEphemeral() || a.Id == b.Id);

    /// <summary>
    /// Membership is cheap; identity is not. Within one identity the tab simply moves. Across identities a live
    /// renderer cannot change its profile, so the destination gets a NEW tab (same URL and title, VIRTUAL, none of
    /// the old cookies/storage) and the original is closed, with its checkpoint, thumbnail, row and index entry
    /// removed. Returns the tab that now represents the resource.
    /// </summary>
    public async Task<VirtualTab> MoveToWorkspaceAsync(ResourceId id, ContextId dest, CancellationToken ct = default)
    {
        RequireOpenWorkspace(dest);
        var t = Find(id);
        var src = _workspaceList.First(w => w.Id == t.WorkspaceId);
        RequireOpenWorkspace(src.Id);
        var dst = _workspaceList.FirstOrDefault(w => w.Id == dest) ?? throw new KeyNotFoundException($"workspace {dest}");
        if (SameIdentity(src, dst))
        {
            t.MoveTo(dest);
            ApplyPinnedOrder(dest);     // it keeps its pin, so it must also keep the place a pin means
            Changed?.Invoke(new("moved", id, dest.ToString()));
            return t;
        }
        var nt = new VirtualTab(ResourceId.New(), t.Url, t.Title, dest);
        nt.SetProtection(t.UserProtection);
        nt.SetPinned(t.IsPinned);
        _tabs.Add(nt);
        Persist(nt);
        Changed?.Invoke(new("opened", nt.Id, nt.Url.Host));
        await CloseAsync(id, ct);
        ApplyPinnedOrder(dest);
        Changed?.Invoke(new("moved", nt.Id, $"identity boundary {src.Container}â†’{dst.Container}: new tab, old one closed"));
        return nt;
    }

    /// <summary>
    /// Switching changes scheduling priority, not renderer lifetime (Â§7). Live tabs of the old workspace stay live
    /// and the Resource OS drains them on its own schedule; the new workspace's last-active tab is activated if any.
    /// </summary>
    public Task SwitchWorkspaceAsync(ContextId ws, CancellationToken ct = default) =>
        SerializedAsync(() => SwitchWorkspaceCoreAsync(ws, ct), ct);

    private async Task SwitchWorkspaceCoreAsync(ContextId ws, CancellationToken ct)
    {
        RequireOpenWorkspace(ws);
        if (ws == ActiveWorkspace) return;
        RecordContextCheckpoint();
        if (Active is not null && Active.State == ResourceState.Hot)
        {
            Active.TryTransition(ResourceState.Warm, Cause.User, _clock());
            Hide(Active);
            Persist(Active);
        }
        Active = null;
        ActiveWorkspace = ws;
        var next = TabsIn(ws).OrderByDescending(t => _lastActive.GetValueOrDefault(t.Id, DateTimeOffset.MinValue)).FirstOrDefault();
        Changed?.Invoke(new("workspace-switched", default, ws.ToString()));
        if (next is not null) await ActivateCoreAsync(next.Id, ct);
    }

    /// <summary>
    /// Time Travel record of the active workspace. Ephemeral workspaces are never recorded (returned for
    /// in-memory use only), and tabs whose class forbids a durable row are left out of durable timelines.
    /// </summary>
    public ContextCheckpoint RecordContextCheckpoint()
    {
        var ws = _workspaceList.FirstOrDefault(w => w.Id == ActiveWorkspace);
        var ephemeral = ws?.Container.IsEphemeral() == true;
        var entries = TabsIn(ActiveWorkspace)
            .Where(t => ephemeral || May(t, DataOperation.PersistTabRow).Allowed)
            .Select(t => new ContextCheckpointEntry(t.Id, t.Url, t.Title, t.State.HasLiveRenderer())).ToList();
        var cp = new ContextCheckpoint(Guid.NewGuid(), ActiveWorkspace, ws?.Name ?? "Default", _clock(), Active?.Id, entries, entries.Count(e => e.WasLive));
        if (!ephemeral) _workspaces?.SaveCheckpoint(cp);
        return cp;
    }

    public IReadOnlyList<ContextCheckpoint> Timeline() => _workspaces?.ListCheckpoints() ?? [];

    /// <summary>
    /// Restore a context lazily: tabs that still exist are left alone, missing ones are recreated VIRTUAL, and only
    /// the checkpoint's active resource gets a renderer.
    /// </summary>
    public Task<int> RestoreContextAsync(ContextCheckpoint cp, CancellationToken ct = default) =>
        SerializedAsync(async () =>
        {
            if (_endedPrivateSessions.Contains(cp.WorkspaceId)) throw new InvalidOperationException("This private session has ended.");
            int recreated = 0;
            foreach (var e in cp.Resources)
            {
                if (_tabs.Any(t => t.Id == e.Id)) continue;
                var t = new VirtualTab(e.Id, e.Url, e.Title, cp.WorkspaceId);
                _tabs.Add(t);
                _repo.Upsert(t, _tabs.Count - 1);
                recreated++;
            }
            if (_workspaceList.All(w => w.Id != cp.WorkspaceId))
            {
                var w = new Workspace(cp.WorkspaceId, cp.WorkspaceName) { CreatedAt = _clock() };
                _workspaceList.Add(w);
                _workspaces?.Upsert(w);
            }
            Changed?.Invoke(new("context-restored", default, $"{recreated} recreated"));
            await SwitchWorkspaceCoreAsync(cp.WorkspaceId, ct);
            if (cp.ActiveResource is { } a && _tabs.Any(t => t.Id == a)) await ActivateCoreAsync(a, ct);
            return recreated;
        }, ct);

    private double PriorityOf(VirtualTab t) =>
        t.WorkspaceId == ActiveWorkspace ? 1.0 : _workspaceList.FirstOrDefault(w => w.Id == t.WorkspaceId)?.BackgroundPriority ?? 0.3;

    public int LiveCount => _leases.LiveResources.Count;
    /// <summary>Milliseconds from activation of a VIRTUAL tab to its page load completing. Feeds the restore p50/p95 metric.</summary>
    public List<double> RestoreTimingsMs { get; } = [];
    public event Action<KernelEvent>? Changed;

    /// <summary>Load durable tabs from storage. Every row comes back VIRTUAL (no renderer survives a restart).</summary>
    public void Load()
    {
        _tabs.Clear();
        _workspaceList.Clear();
        if (_workspaces is not null) _workspaceList.AddRange(_workspaces.LoadAll());
        if (_workspaceList.Count == 0) _workspaceList.Add(new Workspace(ContextId.Default, "Default"));
        foreach (var row in _repo.LoadAll())
        {
            var t = new VirtualTab(row.Id, row.Url, row.Title, row.WorkspaceId);
            t.SetProtection(row.Protection);
            t.SetPinned(row.Pinned);
            if (row.State == ResourceState.Archived) t.TryTransition(ResourceState.Archived, Cause.Recovery, row.LastStateChange);
            _tabs.Add(t);
        }
        Changed?.Invoke(new("loaded", default, $"{_tabs.Count} tabs"));
    }

    public VirtualTab Open(Uri url)
    {
        RequireOpenWorkspace(ActiveWorkspace);
        var t = new VirtualTab(ResourceId.New(), url, "", ActiveWorkspace);
        _tabs.Add(t);
        Persist(t);
        Changed?.Invoke(new("opened", t.Id, url.Host));
        return t;
    }

    public Checkpoint? GetCheckpoint(ResourceId id) => _checkpoints.Get(id);

    /// <summary>
    /// What we managed to preserve the last time this tab was put to sleep, so the UI can promise the user's place
    /// back only when we actually kept it.
    /// </summary>
    public CaptureResult? LastCapture(ResourceId id) => _lastCapture.GetValueOrDefault(id);

    /// <summary>The last automated decision that touched a tab, for "explain why" (Â§11.1).</summary>
    public ScheduledAction? LastDecision(ResourceId id) => _lastDecision.GetValueOrDefault(id);

    /// <summary>Renderer-free snapshot for the Resource OS.</summary>
    public IReadOnlyList<ResourceRuntime> Snapshot() =>
        _tabs.Select(t => new ResourceRuntime(
            t.Id, t.State, t.Protection, Active?.Id == t.Id,
            _lastActive.GetValueOrDefault(t.Id, t.LastStateChange), t.LastStateChange,
            _visits.GetValueOrDefault(t.Id), PriorityOf(t))).ToList();

    /// <summary>
    /// Execute a scheduler plan. Protection is re-checked inside the virtualize path at execution time, so a plan can
    /// never override a veto that appeared after it was computed. Returns the number of tabs virtualized.
    /// </summary>
    public Task<int> ApplyPlanAsync(ResourcePlan plan, CancellationToken ct = default) =>
        SerializedAsync(async () =>
        {
            _leases.MaxLive = Math.Max(1, plan.TargetLiveRenderers);
            int applied = 0;
            foreach (var a in plan.Virtualize)
            {
                if (_tabs.All(t => t.Id != a.Id)) continue;
                var r = await VirtualizeCoreAsync(a.Id, Cause.Scheduler, ct);
                _lastDecision[a.Id] = r.Allowed ? a : a with { Reasons = new Dictionary<string, string>(a.Reasons) { ["executed"] = "no: " + r.Reason } };
                if (r.Allowed) applied++;
                Changed?.Invoke(new("decision", a.Id, r.Allowed ? "virtualized" : "vetoed at execution: " + r.Reason));
            }
            foreach (var s in plan.Skipped)
                if (_tabs.Any(t => t.Id == s.Id && !_endedPrivateSessions.Contains(t.WorkspaceId))) _lastDecision[s.Id] = s;
            return applied;
        }, ct);

    /// <summary>Opens a tab in a named workspace WITHOUT switching to it. Open() is for the person; this is for work done on their behalf.</summary>
    public VirtualTab OpenIn(ContextId workspace, Uri url)
    {
        RequireOpenWorkspace(workspace);
        var t = new VirtualTab(ResourceId.New(), url, "", workspace);
        _tabs.Add(t);
        Persist(t);
        Changed?.Invoke(new("opened", t.Id, url.Host));
        return t;
    }

    /// <summary>
    /// Brings a page to life without showing it: the active tab, the active workspace and what is on screen are left exactly as
    /// they are. This is how an agent's pages run. The person's view is theirs; watching an agent is a choice they make
    /// (ActivateAsync on its page), never something the agent does to them. If the tab is the one being shown, nothing changes.
    /// </summary>
    public Task<bool> EnsureLiveInBackgroundAsync(ResourceId id, CancellationToken ct = default) =>
        SerializedAsync(() => EnsureLiveInBackgroundCoreAsync(id, ct), ct);

    /// <returns>True when the page is live. False when it could not be admitted without exceeding the pool or costing the person the
    /// page they are looking at; the tab is left as it was and the caller must defer or refuse the work.</returns>
    private async Task<bool> EnsureLiveInBackgroundCoreAsync(ResourceId id, CancellationToken ct)
    {
        var tab = Find(id);
        RequireOpenWorkspace(tab.WorkspaceId);
        if (Active?.Id == id) return true;
        var lease = await EnsureLeaseAsync(tab, RenderIntent.Background, ct);
        if (lease is null) { Changed?.Invoke(new("background-refused", id, "the pool is full and only the page being shown could be released", true)); return false; }
        if (lease.IsSuspended) lease.Resume();
        lease.AllowThumbnails = May(tab, DataOperation.PersistThumbnail).Allowed;
        lease.SetVisible(false);
        if (!tab.State.HasLiveRenderer()) tab.TryTransition(ResourceState.Warm, Cause.User, _clock());
        Persist(tab);
        Changed?.Invoke(new("warmed", id, $"live={LiveCount}", true));
        return true;
    }

    /// <summary>
    /// Gives a tab a live renderer, wired to the kernel's events, and returns it. Shared by everything that brings a page back
    /// (the person activating a tab; an agent's page coming live in the background). It decides nothing about which tab is
    /// active or visible: that is the caller's.
    /// </summary>
    private async Task<IRendererLease?> EnsureLeaseAsync(VirtualTab tab, RenderIntent intent, CancellationToken ct)
    {
        var id = tab.Id;
        var background = intent != RenderIntent.Foreground;
        if (_leases.TryGet(id, out var existing)) return existing;
        IRendererLease lease;
        if (!await MakeRoomAsync(id, ct, background)) return null;   // background work is refused rather than allowed to cost the person a page
        // The tab's durable URL is the truth. A checkpoint only contributes scroll, and only when it describes
        // the very page we are about to load; an older checkpoint must never override newer navigation.
        var checkpoint = _checkpoints.Get(id);
        if (checkpoint is not null && checkpoint.Url != tab.Url) checkpoint = null;
        var sw = Stopwatch.StartNew();
        _restoreTimers[id] = sw;
        // Say we are bringing it back before the wait begins, and say whether the place we saved is coming with it.
        Changed?.Invoke(new("restoring", id, checkpoint is null ? "no saved position" : "with saved position", background));
        var ws = _workspaceList.FirstOrDefault(w => w.Id == tab.WorkspaceId);
        lease = await _leases.AcquireAsync(id, tab.Url, intent, ContainerOf(tab), tab.WorkspaceId, ct);
        lease.AllowThumbnails = May(tab, DataOperation.PersistThumbnail).Allowed;
        bool IsCurrent() => !_endedPrivateSessions.Contains(tab.WorkspaceId)
            && _tabs.Contains(tab) && _leases.TryGet(id, out var current) && ReferenceEquals(current, lease);
        lease.NavigationChanged += n =>
        {
            if (!IsCurrent()) return;
            // about:blank is never a destination the user chose. Locally rendered pages (jev://) report it
            // because the content was pushed into the renderer rather than fetched, and treating that as a
            // navigation would overwrite the tab's real address and discard its checkpoint.
            if (n.Url.Scheme == "about") return;
            if (n.Url != tab.Url)
            {
                // A real navigation: the old page's advisory, checkpoint and thumbnail no longer describe this tab.
                _advisory.Remove(tab.Id);
                _checkpoints.Delete(tab.Id);
                DeleteThumb(ThumbPath(tab.Id));
            }
            tab.UpdateNavigation(n.Url, n.Title);
            EnforcePolicy(tab);
            Persist(tab);
            Changed?.Invoke(new("navigated", tab.Id, n.Title));
        };
        lease.DetectedProtectionChanged += f => { if (!IsCurrent()) return; tab.SetDetected(f); Changed?.Invoke(new("protection", tab.Id, f.ToString())); };
        lease.PageSignalsChanged += s =>
        {
            if (!IsCurrent()) return;
            _signals[tab.Id] = s;
            EnforcePolicy(tab); // a password field appearing must purge what was allowed a moment ago
            Changed?.Invoke(new("signals", tab.Id, ClassOf(tab).ToString()));
        };
        lease.Loaded += () =>
        {
            if (!IsCurrent()) return;
            if (_restoreTimers.Remove(id, out var timer))
            {
                RestoreTimingsMs.Add(timer.Elapsed.TotalMilliseconds);
                // A tab with no saved place is loading for the first time, not waking up; the suffix lets the status line
                // avoid announcing a wake-up that did not happen. Consumers key on the event kind, not this text.
                Changed?.Invoke(new("restored", id, $"{timer.ElapsedMilliseconds} ms" + (checkpoint is null ? FirstLoadSuffix : ""), background));
            }
            Changed?.Invoke(new("loaded", id, tab.Url.ToString(), background)); // every completed navigation, for the indexer
        };
        if (checkpoint is not null) lease.ApplyCheckpoint(checkpoint);
        return lease;
    }

    public Task ActivateAsync(ResourceId id, CancellationToken ct = default) =>
        SerializedAsync(() => ActivateCoreAsync(id, ct), ct);

    private async Task ActivateCoreAsync(ResourceId id, CancellationToken ct)
    {
        var tab = Find(id);
        RequireOpenWorkspace(tab.WorkspaceId);
        var now = _clock();
        if (tab.WorkspaceId != ActiveWorkspace) { ActiveWorkspace = tab.WorkspaceId; Changed?.Invoke(new("workspace-switched", default, ActiveWorkspace.ToString())); }

        if (Active is not null && Active.Id != id && Active.State == ResourceState.Hot)
        {
            Active.TryTransition(ResourceState.Warm, Cause.User, now);
            Hide(Active);
            Persist(Active);
        }

        var lease = (await EnsureLeaseAsync(tab, RenderIntent.Foreground, ct))!;   // foreground admission never refuses
        if (lease.IsSuspended) lease.Resume();
        lease.AllowThumbnails = May(tab, DataOperation.PersistThumbnail).Allowed;
        lease.SetVisible(true);

        tab.TryTransition(ResourceState.Hot, Cause.User, now);
        _lastActive[id] = now;
        _visits[id] = _visits.GetValueOrDefault(id) + 1;
        Active = tab;
        Persist(tab);
        Changed?.Invoke(new("activated", id, $"live={LiveCount}"));
    }

    /// <summary>
    /// Capture a checkpoint and keep only what Trust OS allows. The outcome is preserved so callers can tell
    /// "nothing may be persisted" (policy) apart from "we could not preserve the page" (failure/timeout).
    /// </summary>
    private async Task<CaptureResult> CaptureAllowedAsync(VirtualTab tab, IRendererLease lease, CancellationToken ct)
    {
        CaptureResult r;
        try { r = await lease.CaptureCheckpointAsync(_thumbnailDir, ct); }
        catch (OperationCanceledException) { return new(null, CaptureOutcome.Cancelled, "cancelled"); }
        catch (Exception ex) { r = new(null, CaptureOutcome.Failed, ex.Message); }

        if (!r.IsUsable) { Changed?.Invoke(new("checkpoint-failed", tab.Id, $"{r.Outcome}: {r.Detail}")); return r; }

        var cp = r.Checkpoint!;
        if (!May(tab, DataOperation.PersistCheckpoint).Allowed) { DeleteThumb(cp.ThumbnailPath); return r with { Checkpoint = null, Detail = "policy: nothing about this page is persisted" }; }
        if (cp.ThumbnailPath is not null && !May(tab, DataOperation.PersistThumbnail).Allowed) { DeleteThumb(cp.ThumbnailPath); cp = cp with { ThumbnailPath = null }; }
        return r with { Checkpoint = cp };
    }

    /// <summary>
    /// Virtualize = checkpoint â†’ commit â†’ dispose renderer (Â§11.1).
    /// - An automatic demotion whose capture fails is ABORTED and the renderer kept: losing the checkpoint of an
    ///   unfinished page is not something a scheduler may decide. An explicit user request may proceed without one.
    /// - If the durable commit fails the in-memory state is rolled back and the renderer is left alone.
    /// </summary>
    public Task<TransitionResult> VirtualizeAsync(ResourceId id, Cause cause, CancellationToken ct = default) =>
        SerializedAsync(() => VirtualizeCoreAsync(id, cause, ct), ct);

    private async Task<TransitionResult> VirtualizeCoreAsync(ResourceId id, Cause cause, CancellationToken ct)
    {
        var tab = _tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return new(false, ResourceState.Virtual, "tab_closed");
        if (_endedPrivateSessions.Contains(tab.WorkspaceId)) return new(false, tab.State, "private_session_ended");
        if (!tab.State.HasLiveRenderer()) return new(true, tab.State, "already virtual");
        if (cause != Cause.User && tab.IsDemotionVetoed)
            return new(false, tab.State, $"vetoed by protection: {tab.Protection}");

        var now = _clock();
        var path = tab.State switch
        {
            ResourceState.Hot => new[] { ResourceState.Warm, ResourceState.Cold, ResourceState.Virtual },
            ResourceState.Warm => [ResourceState.Cold, ResourceState.Virtual],
            _ => [ResourceState.Virtual],
        };

        // 1. checkpoint (renderer still live)
        Checkpoint? cp = null;
        if (_leases.TryGet(id, out var lease))
        {
            var capture = await CaptureAllowedAsync(tab, lease, ct);
            _lastCapture[id] = capture;
            // An automatic demotion may only proceed when the page was actually preserved. A policy decision not to
            // persist ("Captured", nothing kept) is fine; not knowing whether we preserved it is not.
            if (cause != Cause.User && capture.Outcome is CaptureOutcome.Failed or CaptureOutcome.TimedOut or CaptureOutcome.Cancelled)
                return new(false, tab.State, $"capture_{capture.Outcome.ToString().ToLowerInvariant()}: renderer kept");
            cp = capture.Checkpoint;
        }

        // The tab may have been closed or virtualized by a policy path while we awaited the capture.
        if (!tab.State.HasLiveRenderer() || _tabs.All(t => t.Id != id)) return new(false, tab.State, "changed_during_capture");

        var prevState = tab.State; var prevWhen = tab.LastStateChange; var prevDetected = tab.DetectedProtection;
        foreach (var s in path)
        {
            var r = tab.TryTransition(s, cause, now);
            if (!r.Allowed) { tab.RestoreState(prevState, prevWhen, prevDetected); return r; }
        }

        // 2. commit atomically; on failure roll the model back so it never claims a state that did not happen
        try
        {
            using var tx = _repo.BeginTransaction();
            if (cp is not null && May(tab, DataOperation.PersistTabRow).Allowed) _checkpoints.Upsert(cp);
            Persist(tab);
            tx.Commit();
        }
        catch (Exception ex)
        {
            tab.RestoreState(prevState, prevWhen, prevDetected);
            Changed?.Invoke(new("virtualize-failed", id, ex.Message));
            return new(false, prevState, "commit_failed: renderer kept");
        }
        _signals.Remove(id); // nothing left to detect from

        // 3. dispose
        await _leases.ReleaseAsync(id, ReleaseDisposition.Dispose, ct);
        if (Active?.Id == id) Active = null;
        Changed?.Invoke(new("virtualized", id, cause.ToString()));
        return new(true, tab.State, "virtualized");
    }

    /// <summary>
    /// Refresh the durable checkpoint of every live tab without disposing anything. Called before shutdown so the
    /// next start restores the pages the user was actually on, not the last time a scheduler happened to run.
    /// </summary>
    public Task<int> CheckpointAllAsync(CancellationToken ct = default) =>
        SerializedAsync(async () =>
        {
            int n = 0;
            foreach (var tab in _tabs.Where(t => t.State.HasLiveRenderer()).ToList())
            {
                if (!_leases.TryGet(tab.Id, out var lease)) continue;
                var capture = await CaptureAllowedAsync(tab, lease, ct);
                _lastCapture[tab.Id] = capture;
                if (capture.Checkpoint is null || !May(tab, DataOperation.PersistTabRow).Allowed) continue;
                _checkpoints.Upsert(capture.Checkpoint);
                Persist(tab);
                n++;
            }
            return n;
        }, ct);

    public Task CloseAsync(ResourceId id, CancellationToken ct = default) =>
        SerializedAsync(() => CloseCoreAsync(id, ct), ct);

    private async Task CloseCoreAsync(ResourceId id, CancellationToken ct)
    {
        var tab = Find(id);
        if (_leases.TryGet(id, out _)) await _leases.ReleaseAsync(id, ReleaseDisposition.Dispose, ct);
        _leases.SetNavigationPolicy(id, null);
        _tabs.Remove(tab);
        _lastActive.Remove(id);
        _signals.Remove(id);
        _advisory.Remove(id);
        _restoreTimers.Remove(id);
        _visits.Remove(id);
        _lastDecision.Remove(id);
        _lastCapture.Remove(id);
        if (Active?.Id == id) Active = null;
        var thumb = _checkpoints.Get(id)?.ThumbnailPath;
        _repo.Delete(id); // cascades to checkpoints
        DeleteThumb(thumb);
        DeleteThumb(ThumbPath(id));
        DeleteThumb(ThumbPath(id) + ".tmp");
        Reorder();
        Changed?.Invoke(new("closed", id, ""));
    }

    /// <summary>
    /// Terminal user action, serialized with acquisition and scheduling. Protection never vetoes closing.
    /// This closes tabs and host state only; the shell must separately confirm deletion of engine profile data.
    /// A failed release leaves its tab available for another cleanup attempt, but never for browsing again.
    /// </summary>
    public Task EndPrivateSessionAsync(ContextId workspace, ContextId returnWorkspace, CancellationToken ct = default) =>
        SerializedAsync(async () =>
        {
            var ws = _workspaceList.FirstOrDefault(w => w.Id == workspace);
            if (ws is null && _endedPrivateSessions.Contains(workspace)) return;
            if (ws?.Container != IdentityContainer.Private) throw new InvalidOperationException("Not a private session.");
            RequireOpenWorkspace(returnWorkspace);
            if (workspace == returnWorkspace) throw new InvalidOperationException("Choose another workspace to return to.");
            _endedPrivateSessions.Add(workspace); // before any await: late callbacks cannot recreate state
            if (ActiveWorkspace == workspace)
            {
                Active = null;
                ActiveWorkspace = returnWorkspace;
                Changed?.Invoke(new("workspace-switched", default, returnWorkspace.ToString()));
            }
            List<Exception> failures = [];
            foreach (var tab in TabsIn(workspace).ToList())
            {
                try { await CloseCoreAsync(tab.Id, CancellationToken.None); }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (failures.Count > 0) throw new AggregateException("Private renderer cleanup is incomplete. Retry ending the session.", failures);
            _workspaceList.Remove(ws!);
            Changed?.Invoke(new("workspace-ended", default, workspace.ToString()));
        }, ct);

    public void SetProtection(ResourceId id, ProtectionFlags flags)
    {
        var t = Find(id);
        t.SetProtection(flags);
        Persist(t);
        Changed?.Invoke(new("protection", id, t.UserProtection.ToString()));
    }

    /// <summary>
    /// Placement, and nothing else. A pinned tab still sleeps when the scheduler needs the memory; keeping it awake
    /// is <see cref="ProtectionFlags.KeepActive"/>, a separate choice the user makes separately.
    /// </summary>
    public void SetPinned(ResourceId id, bool pinned)
    {
        var t = Find(id);
        if (t.IsPinned == pinned) return;
        t.SetPinned(pinned);
        ApplyPinnedOrder(t.WorkspaceId);
        Changed?.Invoke(new("pinned", id, pinned ? "pinned" : "unpinned"));
    }

    /// <summary>
    /// Pinned-first is an invariant of workspace membership, not a one-off sort done where the user happens to click.
    /// Anything that adds a tab to a workspace or changes its pin re-establishes it, so a tab can never say "pinned"
    /// while sitting below ordinary tabs and then jump to the top after a restart (which is what the database's
    /// ORDER BY would have done on its own). Only the slots this workspace already occupies are touched: reordering
    /// one workspace must not shuffle another.
    /// </summary>
    private void ApplyPinnedOrder(ContextId workspace)
    {
        var slots = _tabs.Select((x, i) => (x, i)).Where(p => p.x.WorkspaceId == workspace).Select(p => p.i).ToList();
        if (slots.Count == 0) return;
        var sorted = slots.Select(i => _tabs[i]).OrderByDescending(x => x.IsPinned).ToList();   // OrderBy is stable
        for (int k = 0; k < slots.Count; k++) _tabs[slots[k]] = sorted[k];
        foreach (var x in sorted) Persist(x);
    }

    /// <summary>
    /// Foreground admission. Prefers the least-recently-active unprotected tab that has been live at least
    /// AdmissionMinResidency; if every candidate is younger, the budget still wins and the oldest-active goes.
    /// The choice is recorded so "Explain" can say why.
    /// </summary>
    /// <summary>
    /// Makes room for one more renderer. For the person's own action (<paramref name="background"/> false) the budget can be
    /// exceeded if everything live is protected: never losing their work is the higher rule. For work done in the background it
    /// is the other way round: the pool is never exceeded and the tab being shown is never a victim, so this returns false and
    /// the caller must defer or refuse. Nothing an agent asks for is worth the page a person is reading.
    /// </summary>
    private async Task<bool> MakeRoomAsync(ResourceId incoming, CancellationToken ct, bool background = false)
    {
        while (_leases.LiveResources.Count >= _leases.MaxLive)
        {
            var now = _clock();
            var candidates = _tabs
                .Where(t => t.State.HasLiveRenderer() && t.Id != incoming && !t.IsDemotionVetoed && !(background && Active is not null && t.Id == Active.Id))
                .OrderBy(t => _lastActive.GetValueOrDefault(t.Id, DateTimeOffset.MinValue)).ToList();
            var aged = candidates.Where(t => now - t.LastStateChange >= AdmissionMinResidency).ToList();
            var victim = aged.FirstOrDefault() ?? candidates.FirstOrDefault();
            if (victim is null) return !background; // everything live is protected: the person's own action may exceed the budget (Â§19 veto); background work may not
            _lastDecision[victim.Id] = new ScheduledAction(victim.Id, "virtualize", new Dictionary<string, string>
            {
                ["trigger"] = background ? "background_admission" : "foreground_admission",
                ["live_renderers"] = $"{_leases.LiveResources.Count}/{_leases.MaxLive}",
                ["residency_respected"] = (aged.Count > 0).ToString().ToLower(),
                ["jev_consulted"] = "no",
            });
            var r = await VirtualizeCoreAsync(victim.Id, Cause.Scheduler, ct);
            if (!r.Allowed) return !background; // e.g. capture failed: keep the renderer; only the person's own action may exceed the budget
        }
        return true;
    }

    private void Persist(VirtualTab t)
    {
        if (May(t, DataOperation.PersistTabRow).Allowed) _repo.Upsert(t, _tabs.IndexOf(t));
    }

    private void Reorder()
    {
        using var tx = _repo.BeginTransaction();
        for (int i = 0; i < _tabs.Count; i++) Persist(_tabs[i]);
        tx.Commit();
    }

    private static void DeleteThumb(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private VirtualTab Find(ResourceId id) =>
        _tabs.FirstOrDefault(t => t.Id == id) ?? throw new KeyNotFoundException($"tab {id}");
}
