namespace JevBrowse.Domain;

/// <summary>
/// Semantic checkpoint (Architecture §5.1, Table A.5): what is persisted when a renderer is disposed.
/// Deliberately excludes form values, cookies, JS heap, passwords: none of those are ever captured.
/// </summary>
public sealed record Checkpoint(
    ResourceId Id,
    Uri Url,
    string Title,
    double ScrollX,
    double ScrollY,
    string? FaviconUrl,
    string? ThumbnailPath,
    DateTimeOffset CapturedAt);
