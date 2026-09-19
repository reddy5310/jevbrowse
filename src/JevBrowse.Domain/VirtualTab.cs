namespace JevBrowse.Domain;

public readonly record struct ResourceId(Guid Value)
{
    public static ResourceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public enum Cause
{
    /// <summary>User action; always outranks automation.</summary>
    User,
    /// <summary>Deterministic scheduler decision.</summary>
    Scheduler,
    /// <summary>Restore/repair after crash.</summary>
    Recovery,
}

public sealed record TransitionResult(bool Allowed, ResourceState State, string Reason);

/// <summary>
/// Durable logical tab. Owns identity + lifecycle only (Table A.4): no UI, no WebView, no AI provider details.
/// </summary>
public sealed class VirtualTab
{
    // Legal edges of the §19 state machine. Promotion back toward HOT is always allowed (user activates).
    private static readonly Dictionary<ResourceState, ResourceState[]> Demotions = new()
    {
        [ResourceState.Hot] = [ResourceState.Warm],
        [ResourceState.Warm] = [ResourceState.Cold],
        [ResourceState.Cold] = [ResourceState.Suspended, ResourceState.Virtual],
        [ResourceState.Suspended] = [ResourceState.Virtual],
        [ResourceState.Virtual] = [ResourceState.Archived],
        [ResourceState.Archived] = [],
    };

    public VirtualTab(ResourceId id, Uri url, string title = "")
    {
        Id = id;
        Url = url;
        Title = title;
    }

    public ResourceId Id { get; }
    public Uri Url { get; private set; }
    public string Title { get; private set; }
    public ResourceState State { get; private set; } = ResourceState.Virtual;
    /// <summary>Durable, user-chosen flags (pinned, never-hibernate).</summary>
    public ProtectionFlags UserProtection { get; private set; }
    /// <summary>Transient flags detected from the live page (audible, download, dirty form). Cleared when the renderer goes away.</summary>
    public ProtectionFlags DetectedProtection { get; private set; }
    public ProtectionFlags Protection => UserProtection | DetectedProtection;
    public DateTimeOffset LastStateChange { get; private set; } = DateTimeOffset.UtcNow;

    public void UpdateNavigation(Uri url, string title) { Url = url; Title = title; }
    public void SetProtection(ProtectionFlags flags) => UserProtection = flags;
    public void SetDetected(ProtectionFlags flags) => DetectedProtection = flags;

    /// <summary>Whether an automatic (non-user) demotion is currently vetoed.</summary>
    public bool IsDemotionVetoed => Protection != ProtectionFlags.None;

    public TransitionResult TryTransition(ResourceState target, Cause cause, DateTimeOffset now)
    {
        if (target == State)
            return new(true, State, "no-op");

        bool isDemotion = Rank(target) > Rank(State);

        if (isDemotion)
        {
            if (!Demotions[State].Contains(target))
                return new(false, State, $"illegal edge {State}->{target}");

            // Hard rule: protection vetoes automation; only an explicit user action may override.
            if (cause != Cause.User && IsDemotionVetoed)
                return new(false, State, $"vetoed by protection: {Protection}");
        }
        else if (State == ResourceState.Archived && target != ResourceState.Virtual)
        {
            // Archived resources must be un-archived (-> Virtual) before being made live.
            return new(false, State, "archived resources must return to Virtual first");
        }

        State = target;
        LastStateChange = now;
        if (!State.HasLiveRenderer()) DetectedProtection = ProtectionFlags.None; // nothing left to detect from
        return new(true, State, isDemotion ? "demoted" : "promoted");
    }

    private static int Rank(ResourceState s) => (int)s;
}
