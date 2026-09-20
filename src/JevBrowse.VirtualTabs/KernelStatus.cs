namespace JevBrowse.VirtualTabs;

/// <summary>
/// What the status line says about kernel events, in words a person would use.
///
/// The line used to echo every event verbatim ("activated live=2", "virtualized Scheduler", "decision virtualized"):
/// diagnostics wearing the costume of a message, and each one overwrote whatever meaningful thing an action had just
/// said. Now only events a person would want to be told about produce text, and everything else returns null, which
/// means "leave the line alone".
/// </summary>
public static class KernelStatus
{
    public static string? For(KernelEvent e) => e.Background ? null : e.Kind switch
    {
        // Only the automatic case is news. When the user put a tab to sleep themselves, they know.
        "virtualized" when e.Reason == "Scheduler" => "A tab went to sleep to save memory.",
        // A first load is not a wake-up: nothing was asleep, so there is nothing to announce.
        "restored" when e.Reason.EndsWith(TabKernel.FirstLoadSuffix, StringComparison.Ordinal) => null,
        "restored" => $"Woke a sleeping tab in {e.Reason}.",
        "checkpoint-failed" => "A tab was kept open because its place could not be saved first.",
        "virtualize-failed" => "A tab could not be put to sleep.",
        _ => null,
    };
}
