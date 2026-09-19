using System.Diagnostics;
using JevBrowse.Domain;
using JevBrowse.Renderer.Abstractions;
using JevBrowse.ResourceOS;
using JevBrowse.Storage;
using JevBrowse.TrustOS;

namespace JevBrowse.VirtualTabs;

public sealed record KernelEvent(string Kind, ResourceId Id, string Reason);

/// <summary>
/// Virtual Tab Kernel (Architecture §5): owns durable tab identity, ordering, lifecycle, checkpoints and renderer leases.
/// It does not own presentation or scheduling policy; Phase 3's Resource OS will feed it plans.
/// Phase 1 policy is deliberately simple: keep at most MaxLive renderers, evict least-recently-active unprotected tab.
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

    // ---- Trust OS ----

    public IdentityContainer ContainerOf(VirtualTab t) =>
        _workspaceList.FirstOrDefault(w => w.Id == t.WorkspaceId)?.Container ?? IdentityContainer.Personal;

    private readonly Dictionary<ResourceId, DataClass> _advisory = [];

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

    public IReadOnlyList<VirtualTab> Tabs => _tabs;
    public VirtualTab? Active { get; private set; }

    // ---- Context OS ----

    public IReadOnlyList<Workspace> Workspaces => _workspaceList;
    public ContextId ActiveWorkspace { get; private set; } = ContextId.Default;
    public IEnumerable<VirtualTab> TabsIn(ContextId ws) => _tabs.Where(t => t.WorkspaceId == ws);

    public Workspace CreateWorkspace(string name)
    {
        var w = new Workspace(ContextId.New(), name) { CreatedAt = _clock() };
        _workspaceList.Add(w);
        _workspaces?.Upsert(w);
        Changed?.Invoke(new("workspace-created", default, name));
        return w;
    }

    public void MoveToWorkspace(ResourceId id, ContextId ws)
    {
        var t = Find(id);
        t.MoveTo(ws);
        Persist(t);
        Changed?.Invoke(new("moved", id, ws.ToString()));
    }

    /// <summary>
    /// Switching changes scheduling priority, not renderer lifetime (§7). Live tabs of the old workspace stay live
    /// and the Resource OS drains them on its own schedule; the new workspace's last-active tab is activated if any.
    /// </summary>
    public async Task SwitchWorkspaceAsync(ContextId ws, CancellationToken ct = default)
    {
        if (ws == ActiveWorkspace) return;
        RecordContextCheckpoint();
        if (Active is not null && Active.State == ResourceState.Hot)
        {
            Active.TryTransition(ResourceState.Warm, Cause.User, _clock());
            if (_leases.TryGet(Active.Id, out var prev)) prev.SetVisible(false);
            Persist(Active);
        }
        Active = null;
        ActiveWorkspace = ws;
        var next = TabsIn(ws).OrderByDescending(t => _lastActive.GetValueOrDefault(t.Id, DateTimeOffset.MinValue)).FirstOrDefault();
        Changed?.Invoke(new("workspace-switched", default, ws.ToString()));
        if (next is not null) await ActivateAsync(next.Id, ct);
    }

    public ContextCheckpoint RecordContextCheckpoint()
    {
        var ws = _workspaceList.FirstOrDefault(w => w.Id == ActiveWorkspace);
        var entries = TabsIn(ActiveWorkspace).Select(t => new ContextCheckpointEntry(t.Id, t.Url, t.Title, t.State.HasLiveRenderer())).ToList();
        var cp = new ContextCheckpoint(Guid.NewGuid(), ActiveWorkspace, ws?.Name ?? "Default", _clock(), Active?.Id, entries, entries.Count(e => e.WasLive));
        _workspaces?.SaveCheckpoint(cp);
        return cp;
    }

    public IReadOnlyList<ContextCheckpoint> Timeline() => _workspaces?.ListCheckpoints() ?? [];

    /// <summary>
    /// Restore a context lazily: tabs that still exist are left alone, missing ones are recreated VIRTUAL, and only
    /// the checkpoint's active resource gets a renderer.
    /// </summary>
    public async Task<int> RestoreContextAsync(ContextCheckpoint cp, CancellationToken ct = default)
    {
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
        await SwitchWorkspaceAsync(cp.WorkspaceId, ct);
        if (cp.ActiveResource is { } a && _tabs.Any(t => t.Id == a)) await ActivateAsync(a, ct);
        return recreated;
    }

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
            if (row.State == ResourceState.Archived) t.TryTransition(ResourceState.Archived, Cause.Recovery, row.LastStateChange);
            _tabs.Add(t);
        }
        Changed?.Invoke(new("loaded", default, $"{_tabs.Count} tabs"));
    }

    public VirtualTab Open(Uri url)
    {
        var t = new VirtualTab(ResourceId.New(), url, "", ActiveWorkspace);
        _tabs.Add(t);
        Persist(t);
        Changed?.Invoke(new("opened", t.Id, url.Host));
        return t;
    }

    public Checkpoint? GetCheckpoint(ResourceId id) => _checkpoints.Get(id);

    /// <summary>The last automated decision that touched a tab, for "explain why" (§11.1).</summary>
    public ScheduledAction? LastDecision(ResourceId id) => _lastDecision.GetValueOrDefault(id);

    /// <summary>Renderer-free snapshot for the Resource OS.</summary>
    public IReadOnlyList<ResourceRuntime> Snapshot() =>
        _tabs.Select(t => new ResourceRuntime(
            t.Id, t.State, t.Protection, Active?.Id == t.Id,
            _lastActive.GetValueOrDefault(t.Id, t.LastStateChange), t.LastStateChange,
            _visits.GetValueOrDefault(t.Id), PriorityOf(t))).ToList();

    /// <summary>
    /// Execute a scheduler plan. Protection is re-checked inside VirtualizeAsync at execution time, so a plan can
    /// never override a veto that appeared after it was computed. Returns the number of tabs virtualized.
    /// </summary>
    public async Task<int> ApplyPlanAsync(ResourcePlan plan, CancellationToken ct = default)
    {
        _leases.MaxLive = Math.Max(1, plan.TargetLiveRenderers);
        int applied = 0;
        foreach (var a in plan.Virtualize)
        {
            if (_tabs.All(t => t.Id != a.Id)) continue;
            var r = await VirtualizeAsync(a.Id, Cause.Scheduler, ct);
            _lastDecision[a.Id] = r.Allowed ? a : a with { Reasons = new Dictionary<string, string>(a.Reasons) { ["executed"] = "no: " + r.Reason } };
            if (r.Allowed) applied++;
            Changed?.Invoke(new("decision", a.Id, r.Allowed ? "virtualized" : "vetoed at execution: " + r.Reason));
        }
        foreach (var s in plan.Skipped) _lastDecision[s.Id] = s;
        return applied;
    }

    public async Task ActivateAsync(ResourceId id, CancellationToken ct = default)
    {
        var tab = Find(id);
        var now = _clock();
        if (tab.WorkspaceId != ActiveWorkspace) { ActiveWorkspace = tab.WorkspaceId; Changed?.Invoke(new("workspace-switched", default, ActiveWorkspace.ToString())); }

        if (Active is not null && Active.Id != id && Active.State == ResourceState.Hot)
        {
            Active.TryTransition(ResourceState.Warm, Cause.User, now);
            if (_leases.TryGet(Active.Id, out var prev)) prev.SetVisible(false);
            Persist(Active);
        }

        if (!_leases.TryGet(id, out var lease))
        {
            await MakeRoomAsync(id, ct);
            var checkpoint = _checkpoints.Get(id);
            var sw = Stopwatch.StartNew();
            _restoreTimers[id] = sw;
            lease = await _leases.AcquireAsync(id, checkpoint?.Url ?? tab.Url, RenderIntent.Foreground, ContainerOf(tab), ct);
            lease.NavigationChanged += n => { if (n.Url != tab.Url) _advisory.Remove(tab.Id); tab.UpdateNavigation(n.Url, n.Title); Persist(tab); Changed?.Invoke(new("navigated", tab.Id, n.Title)); };
            lease.DetectedProtectionChanged += f => { tab.SetDetected(f); Changed?.Invoke(new("protection", tab.Id, f.ToString())); };
            lease.PageSignalsChanged += s => { _signals[tab.Id] = s; Changed?.Invoke(new("signals", tab.Id, ClassOf(tab).ToString())); };
            lease.Loaded += () =>
            {
                if (_restoreTimers.Remove(id, out var timer))
                {
                    RestoreTimingsMs.Add(timer.Elapsed.TotalMilliseconds);
                    Changed?.Invoke(new("restored", id, $"{timer.ElapsedMilliseconds} ms"));
                }
            };
            if (checkpoint is not null) lease.ApplyCheckpoint(checkpoint);
        }
        if (lease.IsSuspended) lease.Resume();
        lease.SetVisible(true);

        tab.TryTransition(ResourceState.Hot, Cause.User, now);
        _lastActive[id] = now;
        _visits[id] = _visits.GetValueOrDefault(id) + 1;
        Active = tab;
        Persist(tab);
        Changed?.Invoke(new("activated", id, $"live={LiveCount}"));
    }

    /// <summary>
    /// Virtualize = checkpoint → commit → dispose renderer (§11.1). If capture or commit fails the renderer is untouched,
    /// so a crash mid-way can only lose a checkpoint, never a tab.
    /// </summary>
    public async Task<TransitionResult> VirtualizeAsync(ResourceId id, Cause cause, CancellationToken ct = default)
    {
        var tab = Find(id);
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
            try { cp = await lease.CaptureCheckpointAsync(_thumbnailDir, ct); }
            catch (Exception ex) { Changed?.Invoke(new("checkpoint-failed", id, ex.Message)); }
        }

        // Trust OS gate (Table A.10): the class decides what of the capture may survive the renderer.
        if (cp is not null)
        {
            if (!May(tab, DataOperation.PersistCheckpoint).Allowed) { DeleteThumb(cp.ThumbnailPath); cp = null; }
            else if (cp.ThumbnailPath is not null && !May(tab, DataOperation.PersistThumbnail).Allowed) { DeleteThumb(cp.ThumbnailPath); cp = cp with { ThumbnailPath = null }; }
        }

        foreach (var s in path)
        {
            var r = tab.TryTransition(s, cause, now);
            if (!r.Allowed) return r;
        }

        // 2. commit atomically
        using (var tx = _repo.BeginTransaction())
        {
            if (cp is not null && May(tab, DataOperation.PersistTabRow).Allowed) _checkpoints.Upsert(cp);
            Persist(tab);
            tx.Commit();
        }
        _signals.Remove(id); // nothing left to detect from

        // 3. dispose
        await _leases.ReleaseAsync(id, ReleaseDisposition.Dispose, ct);
        if (Active?.Id == id) Active = null;
        Changed?.Invoke(new("virtualized", id, cause.ToString()));
        return new(true, tab.State, "virtualized");
    }

    public async Task CloseAsync(ResourceId id, CancellationToken ct = default)
    {
        var tab = Find(id);
        if (_leases.TryGet(id, out _)) await _leases.ReleaseAsync(id, ReleaseDisposition.Dispose, ct);
        _tabs.Remove(tab);
        _lastActive.Remove(id);
        if (Active?.Id == id) Active = null;
        var thumb = _checkpoints.Get(id)?.ThumbnailPath;
        _repo.Delete(id); // cascades to checkpoints
        if (thumb is not null) { try { File.Delete(thumb); } catch (IOException) { } }
        Reorder();
        Changed?.Invoke(new("closed", id, ""));
    }

    public void SetProtection(ResourceId id, ProtectionFlags flags)
    {
        var t = Find(id);
        t.SetProtection(flags);
        Persist(t);
    }

    /// <summary>Phase 1 eviction: least-recently-active, unprotected, live tab that is not the one being activated.</summary>
    private async Task MakeRoomAsync(ResourceId incoming, CancellationToken ct)
    {
        while (_leases.LiveResources.Count >= _leases.MaxLive)
        {
            var victim = _tabs
                .Where(t => t.State.HasLiveRenderer() && t.Id != incoming && !t.IsDemotionVetoed)
                .OrderBy(t => _lastActive.GetValueOrDefault(t.Id, DateTimeOffset.MinValue))
                .FirstOrDefault();
            if (victim is null) break; // everything live is protected; pool may temporarily exceed budget (§19 veto)
            await VirtualizeAsync(victim.Id, Cause.Scheduler, ct);
        }
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
