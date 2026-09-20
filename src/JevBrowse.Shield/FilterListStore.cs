using System.Security.Cryptography;

namespace JevBrowse.Shield;

/// <summary>
/// Manages filter lists on disk (§16 `filters\`). Layout:
///   filters/active/&lt;name&gt;.txt     currently compiled lists
///   filters/previous/&lt;name&gt;.txt   last known-good, for rollback
///   filters/staging/                 downloads land here; promoted only after validation
/// Activation is a directory swap so a crash mid-update can never leave a half-written active list (Table A.11).
/// This is the only network-touching code in Shield and it is invoked explicitly, never on the request hot path.
/// </summary>
public sealed class FilterListStore
{
    public sealed record ListSource(string Name, Uri Url);

    /// <summary>Default sources. Both are documented in docs/privacy/NETWORK_CALLS.md.</summary>
    public static readonly ListSource[] DefaultSources =
    [
        new("easylist", new Uri("https://easylist.to/easylist/easylist.txt")),
        new("easyprivacy", new Uri("https://easylist.to/easylist/easyprivacy.txt")),
    ];

    private readonly string _root;

    public FilterListStore(string root)
    {
        _root = root;
        Recover();
    }

    /// <summary>
    /// Activation is two directory renames (active→previous, staging→active), which is not atomic as a pair. A crash
    /// between them leaves no `active` directory. On startup: if `active` is missing, restore the last known-good
    /// `previous`; and always discard `staging`, which may be a half-written download and is never trusted.
    /// </summary>
    public bool Recover()
    {
        bool restored = false;
        try
        {
            if (!Directory.Exists(ActiveDir) && Directory.Exists(PreviousDir)) { Directory.Move(PreviousDir, ActiveDir); restored = true; }
            if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return restored;
    }

    public string ActiveDir => Path.Combine(_root, "active");
    public string PreviousDir => Path.Combine(_root, "previous");
    public string StagingDir => Path.Combine(_root, "staging");

    public bool HasActiveLists => Directory.Exists(ActiveDir) && Directory.EnumerateFiles(ActiveDir, "*.txt").Any();

    public IEnumerable<string> ReadActiveLines()
    {
        if (!Directory.Exists(ActiveDir)) yield break;
        foreach (var f in Directory.EnumerateFiles(ActiveDir, "*.txt").OrderBy(x => x))
            foreach (var line in File.ReadLines(f)) yield return line;
    }

    public sealed record UpdateResult(bool Activated, IReadOnlyDictionary<string, string> Details);

    /// <summary>Download all sources into staging, validate each, then swap. Any failure leaves `active` untouched.</summary>
    public async Task<UpdateResult> UpdateAsync(HttpClient http, IEnumerable<ListSource>? sources = null, CancellationToken ct = default)
    {
        var details = new Dictionary<string, string>();
        if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, recursive: true);
        Directory.CreateDirectory(StagingDir);

        foreach (var src in sources ?? DefaultSources)
        {
            try
            {
                using var resp = await http.GetAsync(src.Url, ct);
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync(ct);
                var (rules, skipped) = Validate(text);
                if (rules < 1000) { details[src.Name] = $"rejected: only {rules} rules parsed"; return new(false, details); }
                await File.WriteAllTextAsync(Path.Combine(StagingDir, src.Name + ".txt"), text, ct);
                details[src.Name] = $"{rules} rules, {skipped} skipped, sha256 {Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12]}";
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                details[src.Name] = "failed: " + ex.Message;
                return new(false, details);
            }
        }

        // Promote: active -> previous, staging -> active. Two renames; the second cannot fail for reasons the first didn't.
        if (Directory.Exists(PreviousDir)) Directory.Delete(PreviousDir, recursive: true);
        if (Directory.Exists(ActiveDir)) Directory.Move(ActiveDir, PreviousDir);
        Directory.Move(StagingDir, ActiveDir);
        return new(true, details);
    }

    public bool Rollback()
    {
        if (!Directory.Exists(PreviousDir)) return false;
        if (Directory.Exists(ActiveDir)) Directory.Delete(ActiveDir, recursive: true);
        Directory.Move(PreviousDir, ActiveDir);
        return true;
    }

    public static (int Rules, int Skipped) Validate(string text)
    {
        int rules = 0, skipped = 0;
        foreach (var line in text.Split('\n'))
            if (FilterRule.TryParse(line, out _)) rules++; else if (line.Trim().Length > 0 && line[0] != '!') skipped++;
        return (rules, skipped);
    }
}
