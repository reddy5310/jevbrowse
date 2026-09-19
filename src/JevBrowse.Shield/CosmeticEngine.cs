using System.Text;

namespace JevBrowse.Shield;

/// <summary>
/// Element-hiding rules (`##`) from EasyList-style lists (§9 "cosmetic/annoyance rules"). Deterministic and
/// conservative: procedural/extended selectors (`#?#`, `:has(`, `:-abp-`, `:xpath(`, `:style(`) are skipped, not
/// approximated. Output is one stylesheet per page: generic selectors plus the selectors scoped to the page's domain,
/// minus that domain's exceptions (`#@#`). Everything hidden is recoverable per site with one click.
/// </summary>
public sealed class CosmeticEngine
{
    private readonly HashSet<string> _generic = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _byDomain = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _exceptByDomain = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _genericExceptions = new(StringComparer.Ordinal);
    private string? _genericCss;

    public int GenericCount => _generic.Count;
    public int DomainRuleCount => _byDomain.Sum(kv => kv.Value.Count);
    public int Skipped { get; private set; }

    public static CosmeticEngine Compile(IEnumerable<string> lines)
    {
        var e = new CosmeticEngine();
        foreach (var raw in lines) e.Add(raw);
        e._genericCss = null;
        return e;
    }

    public void Add(string raw)
    {
        var line = raw.Trim();
        if (line.Contains("#?#") || line.Contains("#$#") || line.Contains("#%#") || line.Contains("#@?#")) { Skipped++; return; } // procedural / scriptlet / snippet
        int i = line.IndexOf("#@#", StringComparison.Ordinal);
        bool exception = i >= 0;
        if (!exception) i = line.IndexOf("##", StringComparison.Ordinal);
        if (i < 0) return;
        var selector = line[(i + (exception ? 3 : 2))..].Trim();
        if (!IsPlainSelector(selector)) { Skipped++; return; }
        var domains = line[..i];

        if (domains.Length == 0)
        {
            if (exception) _genericExceptions.Add(selector); else _generic.Add(selector);
            return;
        }
        foreach (var d in domains.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var dom = d.Trim();
            if (dom.StartsWith('~'))
            {
                // "~example.com##x" = generic except on example.com; model as generic + exception.
                if (!exception) { _generic.Add(selector); Bucket(_exceptByDomain, dom[1..]).Add(selector); }
                continue;
            }
            Bucket(exception ? _exceptByDomain : _byDomain, dom).Add(selector);
        }
    }

    /// <summary>Stylesheet for a page host: generic + domain-scoped selectors, minus exceptions. Cached per host by the caller.</summary>
    public string StylesheetFor(string host)
    {
        host = host.ToLowerInvariant();
        var exceptions = new HashSet<string>(_genericExceptions, StringComparer.Ordinal);
        var scoped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (dom, sels) in _byDomain) if (HostMatches(host, dom)) scoped.UnionWith(sels);
        foreach (var (dom, sels) in _exceptByDomain) if (HostMatches(host, dom)) exceptions.UnionWith(sels);

        if (exceptions.Count == 0 && scoped.Count == 0) return _genericCss ??= Build(_generic);
        var all = new HashSet<string>(_generic, StringComparer.Ordinal);
        all.UnionWith(scoped);
        all.ExceptWith(exceptions);
        return Build(all);
    }

    public int SelectorCountFor(string host) => StylesheetFor(host).Count(c => c == '\n');

    private static string Build(IEnumerable<string> selectors)
    {
        // One rule per line keeps a single bad selector from invalidating the rest (browsers drop the whole rule list
        // for a syntax error in a comma-joined selector).
        var sb = new StringBuilder();
        foreach (var s in selectors) sb.Append(s).Append("{display:none!important;}\n");
        return sb.ToString();
    }

    private static HashSet<string> Bucket(Dictionary<string, HashSet<string>> map, string key) =>
        map.TryGetValue(key, out var set) ? set : map[key] = new HashSet<string>(StringComparer.Ordinal);

    private static bool HostMatches(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    private static bool IsPlainSelector(string s)
    {
        if (s.Length == 0 || s.Length > 400) return false;
        if (s.Contains(":has(") || s.Contains(":-abp-") || s.Contains(":xpath(") || s.Contains(":style(") || s.Contains(":matches-css") || s.Contains(":upward(") || s.Contains(":remove(") || s.Contains(":contains(") || s.Contains(":if(")) return false;
        if (s.Contains('{') || s.Contains('}') || s.Contains(';')) return false;
        return true;
    }
}
