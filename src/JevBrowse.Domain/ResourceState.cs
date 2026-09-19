namespace JevBrowse.Domain;

/// <summary>Lifecycle of a logical resource (Architecture §5). Only HOT/WARM/COLD/SUSPENDED hold a live renderer.</summary>
public enum ResourceState
{
    Hot,
    Warm,
    Cold,
    Suspended,
    Virtual,
    Archived,
}

public static class ResourceStateExtensions
{
    /// <summary>Core invariant: VISIBLE RESOURCE != LIVE RENDERER. Virtual/Archived never own a renderer.</summary>
    public static bool HasLiveRenderer(this ResourceState s) =>
        s is ResourceState.Hot or ResourceState.Warm or ResourceState.Cold or ResourceState.Suspended;
}
