using System.Diagnostics;

namespace JevBrowse.Diagnostics;

/// <summary>One point-in-time sample. All values are MEASURED from the OS, not estimated (Architecture §15).</summary>
public sealed record MemorySample(
    DateTimeOffset At,
    int ProcessCount,
    long WorkingSetBytes,
    long PrivateBytes)
{
    public double WorkingSetMb => WorkingSetBytes / 1048576.0;
    public double PrivateMb => PrivateBytes / 1048576.0;
}

/// <summary>
/// Measures the memory of a process group. Deliberately takes plain PIDs so it has no WebView2 dependency;
/// the renderer adapter supplies the PIDs. Process count is reported because site isolation means
/// WebView count != renderer count.
/// </summary>
public static class ProcessGroupProbe
{
    public static MemorySample Sample(IEnumerable<int> pids)
    {
        long ws = 0, priv = 0;
        int n = 0;
        foreach (var pid in pids.Distinct())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                ws += p.WorkingSet64;
                priv += p.PrivateMemorySize64;
                n++;
            }
            catch (ArgumentException) { /* exited between enumeration and sampling */ }
            catch (InvalidOperationException) { }
        }
        return new MemorySample(DateTimeOffset.UtcNow, n, ws, priv);
    }
}
