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

public sealed record CaptureResult(Checkpoint? Checkpoint, CaptureOutcome Outcome, string Detail)
{
    /// <summary>True when the checkpoint is good enough to promise the user their place was kept.</summary>
    public bool IsUsable => Checkpoint is not null && Outcome is CaptureOutcome.Captured or CaptureOutcome.Partial;
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
    DateTimeOffset CapturedAt);
