using JevBrowse.Domain;
using JevBrowse.Renderer.Abstractions;
using JevBrowse.Storage;

namespace JevBrowse.VirtualTabs;

public sealed record KernelEvent(string Kind, ResourceId Id, string Reason);

/// <summary>
/// Virtual Tab Kernel (Architecture §5): owns durable tab identity, ordering, lifecycle and renderer leases.
/// It does not own presentation or scheduling policy; Phase 3's Resource OS will feed it plans.
/// Phase 1 policy is deliberately simple: keep at most MaxLive renderers, evict least-recently-active unprotected tab.
/// </summary>
public sealed class TabKernel
{
    private readonly List<VirtualTab> _tabs = [];
    private readonly Dictionary<ResourceId, DateTimeOffset> _lastActive = [];
    private readonly IRendererLeaseManager _leases;
    private readonly TabRepository _repo;
    private readonly Func<DateTimeOffset> _clock;

    public TabKernel(IRendererLeaseManager leases, TabRepository repo, Func<DateTimeOffset>? clock = null)
    {
        _leases = leases;
        _repo = repo;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<VirtualTab> Tabs => _tabs;
    public VirtualTab? Active { get; private set; }
    public int LiveCount => _leases.LiveResources.Count;
    public event Action<KernelEvent>? Changed;

    /// <summary>Load durable tabs from storage. Every row comes back VIRTUAL (no renderer survives a restart).</summary>
    public void Load()
    {
        _tabs.Clear();
        foreach (var row in _repo.LoadAll())
        {
            var t = new VirtualTab(row.Id, row.Url, row.Title);
            t.SetProtection(row.Protection);
            if (row.State == ResourceState.Archived) t.TryTransition(ResourceState.Archived, Cause.Recovery, row.LastStateChange);
            _tabs.Add(t);
        }
        Changed?.Invoke(new("loaded", default, $"{_tabs.Count} tabs"));
    }

    public VirtualTab Open(Uri url, bool activate = true)
    {
        var t = new VirtualTab(ResourceId.New(), url);
        _tabs.Add(t);
        _repo.Upsert(t, _tabs.Count - 1);
        Changed?.Invoke(new("opened", t.Id, url.Host));
        return t;
    }

    public async Task ActivateAsync(ResourceId id, CancellationToken ct = default)
    {
        var tab = Find(id);
        var now = _clock();

        if (Active is not null && Active.Id != id && Active.State == ResourceState.Hot)
        {
            Active.TryTransition(ResourceState.Warm, Cause.User, now);
            if (_leases.TryGet(Active.Id, out var prev)) prev.SetVisible(false);
            Persist(Active);
        }

        if (!_leases.TryGet(id, out var lease))
        {
            await MakeRoomAsync(id, ct);
            lease = await _leases.AcquireAsync(id, tab.Url, RenderIntent.Foreground, ct);
            lease.NavigationChanged += n => { tab.UpdateNavigation(n.Url, n.Title); Persist(tab); Changed?.Invoke(new("navigated", tab.Id, n.Title)); };
        }
        if (lease.IsSuspended) lease.Resume();
        lease.SetVisible(true);

        tab.TryTransition(ResourceState.Hot, Cause.User, now);
        _lastActive[id] = now;
        Active = tab;
        Persist(tab);
        Changed?.Invoke(new("activated", id, $"live={LiveCount}"));
    }

    /// <summary>Virtualize: dispose renderer, keep durable state. Vetoed by protection unless the user asks.</summary>
    public async Task<TransitionResult> VirtualizeAsync(ResourceId id, Cause cause, CancellationToken ct = default)
    {
        var tab = Find(id);
        if (!tab.State.HasLiveRenderer()) return new(true, tab.State, "already virtual");
        var now = _clock();

        // Walk the legal path Hot→Warm→Cold→Virtual; a veto anywhere aborts before the renderer is touched.
        var path = tab.State switch
        {
            ResourceState.Hot => new[] { ResourceState.Warm, ResourceState.Cold, ResourceState.Virtual },
            ResourceState.Warm => [ResourceState.Cold, ResourceState.Virtual],
            _ => [ResourceState.Virtual],
        };
        if (cause != Cause.User && tab.IsDemotionVetoed)
            return new(false, tab.State, $"vetoed by protection: {tab.Protection}");
        foreach (var s in path)
        {
            var r = tab.TryTransition(s, cause, now);
            if (!r.Allowed) return r;
        }

        await _leases.ReleaseAsync(id, ReleaseDisposition.Dispose, ct);
        if (Active?.Id == id) Active = null;
        Persist(tab);
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
        _repo.Delete(id);
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

    private void Persist(VirtualTab t) => _repo.Upsert(t, _tabs.IndexOf(t));

    private void Reorder()
    {
        using var tx = _repo.BeginTransaction();
        for (int i = 0; i < _tabs.Count; i++) _repo.Upsert(_tabs[i], i);
        tx.Commit();
    }

    private VirtualTab Find(ResourceId id) =>
        _tabs.FirstOrDefault(t => t.Id == id) ?? throw new KeyNotFoundException($"tab {id}");
}
