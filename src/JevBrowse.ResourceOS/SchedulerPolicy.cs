namespace JevBrowse.ResourceOS;

/// <summary>Memory budget modes, Table A.6.</summary>
public enum MemoryMode { Performance, Balanced, LowRam, Battery, DataSaver, Custom }

/// <summary>Anti-thrashing knobs from Architecture §20. Every number here is user-overridable in Custom mode.</summary>
public sealed record SchedulerPolicy(
    MemoryMode Mode,
    int MaxLive,
    TimeSpan MinResidency,
    TimeSpan Cooldown,
    TimeSpan IdleBeforeVirtualize,
    int MaxHibernationsPerWindow,
    TimeSpan Window,
    bool AllowPrewarm)
{
    public static SchedulerPolicy For(MemoryMode mode) => mode switch
    {
        MemoryMode.Performance => new(mode, 12, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(45), 6, TimeSpan.FromMinutes(5), true),
        MemoryMode.Balanced    => new(mode, 8,  TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(20), 6, TimeSpan.FromMinutes(5), true),
        MemoryMode.LowRam      => new(mode, 3,  TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5),  10, TimeSpan.FromMinutes(5), false),
        MemoryMode.Battery     => new(mode, 5,  TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(10), 6, TimeSpan.FromMinutes(5), false),
        MemoryMode.DataSaver   => new(mode, 8,  TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(20), 6, TimeSpan.FromMinutes(5), false),
        _ => new(mode, 8, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(20), 6, TimeSpan.FromMinutes(5), true),
    };

    /// <summary>Live-renderer budget for a pressure band. RED always collapses to one.</summary>
    public int TargetLive(PressureBand band) => band switch
    {
        PressureBand.Green => MaxLive,
        PressureBand.Yellow => Math.Max(2, MaxLive - 2),
        PressureBand.Orange => Math.Max(1, MaxLive / 2),
        _ => 1,
    };
}
