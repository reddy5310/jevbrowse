namespace JevBrowse.Domain;

/// <summary>
/// Semantic checkpoint (Architecture §5.1, Table A.5): what is persisted when a renderer is disposed.
/// Deliberately excludes form values, cookies, JS heap, passwords: none of those are ever captured.
/// </summary>
/// <summary>
/// What actually happened when we tried to preserve a page. The distinction matters: "we saved everything we
/// promise" and "we ran out of time and kept only the address" must not look the same to the scheduler or the user.
/// </summary>
public enum CaptureOutcome
{
    /// <summary>Everything the page's data class permits was captured.</summary>
    Captured,
    /// <summary>The address and title were captured; something optional (scroll, preview) was not.</summary>
    Partial,
    /// <summary>The page did not answer in time. Whatever it was doing, we did not get a usable checkpoint.</summary>
    TimedOut,
    /// <summary>Shutdown or an explicit cancellation stopped the capture.</summary>
    Cancelled,
    /// <summary>The renderer refused or is gone.</summary>
    Failed,
}

/// <summary>
/// Which parts of a page actually survived. "Partial" alone is not enough to speak accurately: losing the preview
/// image and losing the user's scroll position are different losses, and only the second one changes what the user
/// sees when the page comes back. The UI states what is true from these flags, not from the outcome.
/// </summary>
[Flags]
public enum PreservedParts
{
    None = 0,
    /// <summary>Address and title: without this there is nothing to reopen.</summary>
    Address = 1 << 0,
    /// <summary>Scroll position. Missing means the page reopens at the top.</summary>
    Position = 1 << 1,
    /// <summary>Preview image. Missing means the restore panel has no picture to show; the page is unaffected.</summary>
    Preview = 1 << 2,
}

public sealed record CaptureResult(Checkpoint? Checkpoint, CaptureOutcome Outcome, string Detail, PreservedParts Preserved = PreservedParts.None, NavHistory? History = null)
{
    /// <summary>True when the checkpoint is good enough to promise the user their page was kept.</summary>
    public bool IsUsable => Checkpoint is not null && Outcome is CaptureOutcome.Captured or CaptureOutcome.Partial;

    /// <summary>The address survived, so reopening lands on the right page even if the place within it was lost.</summary>
    public bool HasAddress => Preserved.HasFlag(PreservedParts.Address);

    /// <summary>What to tell the user when this page comes back. Empty when nothing needs saying.</summary>
    public string Shortfall => Outcome switch
    {
        CaptureOutcome.Captured => "",
        CaptureOutcome.Partial when !Preserved.HasFlag(PreservedParts.Position) => "previous position unavailable",
        CaptureOutcome.Partial => "",   // only the preview was lost; the page itself comes back intact
        CaptureOutcome.TimedOut => "the page did not respond in time, so it reopens from the start",
        CaptureOutcome.Cancelled => "saving was interrupted",
        _ => "this page could not be saved",
    };
}

/// <summary>How a restore ended, so the UI can say what is true rather than assuming success.</summary>
public enum RestoreState { Restoring, Restored, RestoredWithoutPlace, TimedOut, Failed }

public sealed record Checkpoint(
    ResourceId Id,
    Uri Url,
    string Title,
    double ScrollX,
    double ScrollY,
    string? FaviconUrl,
    string? ThumbnailPath,
    DateTimeOffset CapturedAt,
    NavHistory? History = null);

/// <summary>One page in a tab's Back/Forward history.</summary>
public sealed record HistoryEntry(string Url, string Title);

/// <summary>A tab's Back/Forward history as it was when the tab went to sleep: every entry, oldest first, and which one was showing.</summary>
public sealed record NavHistory(IReadOnlyList<HistoryEntry> Entries, int Index);
