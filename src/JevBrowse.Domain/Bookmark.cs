using System.Net;
using System.Text.RegularExpressions;

namespace JevBrowse.Domain;

/// <param name="Folder">A folder name from an import, or empty. Bookmarks are a flat list you can search; the folder is kept only so nothing is lost.</param>
public sealed record Bookmark(string Url, string Title, string Folder = "", DateTimeOffset? AddedAt = null);

/// <summary>
/// Reads the bookmark file every major browser can export (the Netscape HTML format: Chrome, Edge, Firefox, Brave, Safari all write it).
/// Pure and forgiving: a damaged or hostile file yields fewer bookmarks, never an exception. Only web addresses are kept, so a file cannot plant
/// a javascript: or file: entry that would run when clicked.
/// </summary>
public static partial class BookmarkImport
{
    public const int MaxBookmarks = 20000;
    public const int MaxFileChars = 20_000_000;

    [GeneratedRegex(@"<(?<close>/)?(?<tag>DL|H3|A)\b(?<attrs>[^>]*)>(?<text>[^<]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Token();

    [GeneratedRegex(@"(?<n>href|add_date)\s*=\s*""(?<v>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Attr();

    public static IReadOnlyList<Bookmark> Parse(string? html)
    {
        var result = new List<Bookmark>();
        if (string.IsNullOrWhiteSpace(html) || html.Length > MaxFileChars) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var folders = new Stack<string>();
        string pending = "";
        try
        {
            foreach (Match m in Token().Matches(html))
            {
                var tag = m.Groups["tag"].Value.ToUpperInvariant();
                var closing = m.Groups["close"].Success;
                var text = WebUtility.HtmlDecode(m.Groups["text"].Value).Trim();
                if (tag == "DL") { if (closing) { if (folders.Count > 0) folders.Pop(); } else folders.Push(pending); pending = ""; }
                else if (tag == "H3" && !closing) pending = text;
                else if (tag == "A" && !closing)
                {
                    string? href = null; DateTimeOffset? added = null;
                    foreach (Match a in Attr().Matches(m.Groups["attrs"].Value))
                    {
                        var v = WebUtility.HtmlDecode(a.Groups["v"].Value);
                        if (a.Groups["n"].Value.Equals("href", StringComparison.OrdinalIgnoreCase)) href = v;
                        else if (long.TryParse(v, out var secs) && secs is > 0 and < 32503680000) added = DateTimeOffset.FromUnixTimeSeconds(secs);
                    }
                    if (href is null || !Uri.TryCreate(href.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || string.IsNullOrEmpty(u.Host)) continue;
                    if (!seen.Add(u.AbsoluteUri)) continue;
                    var folder = string.Join(" / ", folders.Reverse().Where(f => f.Length > 0));
                    result.Add(new Bookmark(u.AbsoluteUri, text.Length == 0 ? u.Host : Trim(text, 200), Trim(folder, 200), added));
                    if (result.Count >= MaxBookmarks) break;
                }
            }
        }
        catch (RegexMatchTimeoutException) { /* a pathological file: keep what was read */ }
        return result;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}
