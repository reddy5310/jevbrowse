using System.Diagnostics;

namespace JevBrowse.Shield;

/// <summary>
/// Compiled, deterministic request filter (§9). Rules are bucketed by their longest literal token; a request only
/// tests rules whose token appears in the URL. Exceptions (`@@`) are checked only when a block rule matched.
/// Unknown → ALLOW. Nothing here is asynchronous, allocates per request beyond the URL string, or calls out.
/// </summary>
public sealed class FilterEngine
{
    private readonly Dictionary<string, List<FilterRule>> _blockByToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FilterRule>> _exceptByToken = new(StringComparer.Ordinal);
    private readonly List<FilterRule> _blockUntokened = [];
    private readonly List<FilterRule> _exceptUntokened = [];

    public int RuleCount { get; private set; }
    public int SkippedLines { get; private set; }

    public static FilterEngine Compile(IEnumerable<string> lines)
    {
        var e = new FilterEngine();
        foreach (var line in lines)
        {
            if (!FilterRule.TryParse(line, out var r) || r is null) { e.SkippedLines++; continue; }
            e.Add(r);
        }
        return e;
    }

    public void Add(FilterRule r)
    {
        RuleCount++;
        var map = r.IsException ? _exceptByToken : _blockByToken;
        if (r.Token.Length >= 3) (map.TryGetValue(r.Token, out var l) ? l : map[r.Token] = []).Add(r);
        else (r.IsException ? _exceptUntokened : _blockUntokened).Add(r);
    }

    public NetworkDecision Evaluate(NetworkRequest req)
    {
        var url = req.Url.AbsoluteUri.ToLowerInvariant();
        var block = FirstMatch(_blockByToken, _blockUntokened, url, req);
        if (block is null) return NetworkDecision.Allowed;
        var except = FirstMatch(_exceptByToken, _exceptUntokened, url, req);
        return except is null ? new NetworkDecision(Verdict.Block, block.Raw) : new NetworkDecision(Verdict.Allow, "@@" + except.Raw);
    }

    private static FilterRule? FirstMatch(Dictionary<string, List<FilterRule>> byToken, List<FilterRule> untokened, string url, NetworkRequest req)
    {
        // Enumerate every substring token present in the URL that is a key. Tokens are alnum runs; try each run and
        // its prefixes down to 3 chars, which is what LongestToken produces for typical rules.
        int i = 0;
        while (i < url.Length)
        {
            if (!IsTokenChar(url[i])) { i++; continue; }
            int j = i;
            while (j < url.Length && IsTokenChar(url[j])) j++;
            for (int a = i; a < j; a++)
                for (int b = j; b - a >= 3; b--)
                {
                    if (byToken.TryGetValue(url[a..b], out var rules))
                        foreach (var r in rules) if (Matches(r, url, req)) return r;
                }
            i = j;
        }
        foreach (var r in untokened) if (Matches(r, url, req)) return r;
        return null;
    }

    private static bool IsTokenChar(char c) => FilterRule.IsTokenChar(c);

    public static bool Matches(FilterRule r, string url, NetworkRequest req)
    {
        if (((int)r.Types & (1 << (int)req.Type)) == 0) return false;
        if (r.ThirdParty is { } tp && tp != req.IsThirdParty) return false;
        if (r.DomainsInclude.Length > 0 || r.DomainsExclude.Length > 0)
        {
            var site = (req.Initiator ?? req.Url).Host.ToLowerInvariant();
            if (r.DomainsExclude.Any(d => HostMatches(site, d))) return false;
            if (r.DomainsInclude.Length > 0 && !r.DomainsInclude.Any(d => HostMatches(site, d))) return false;
        }
        return r.Anchor switch
        {
            Anchor.Domain => MatchDomainAnchored(r.Pattern, url, req.Host),
            Anchor.Start => MatchAt(r.Pattern, url, 0, requireEnd: false),
            Anchor.StartAndEnd => MatchAt(r.Pattern, url, 0, requireEnd: true),
            Anchor.End => MatchAnywhere(r.Pattern, url, requireEnd: true),
            _ => MatchAnywhere(r.Pattern, url, requireEnd: false),
        };
    }

    private static bool HostMatches(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    private static bool MatchDomainAnchored(string pattern, string url, string host)
    {
        // `||example.com/path` matches at the start of the host, or at any subdomain boundary.
        int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        int hostStart = schemeEnd < 0 ? 0 : schemeEnd + 3;
        int hostEnd = url.IndexOfAny(['/', '?', '#'], hostStart);
        if (hostEnd < 0) hostEnd = url.Length;
        for (int p = hostStart; p < hostEnd; p++)
        {
            if (p == hostStart || url[p - 1] == '.')
                if (MatchAt(pattern, url, p, requireEnd: false)) return true;
        }
        return false;
    }

    private static bool MatchAnywhere(string pattern, string url, bool requireEnd)
    {
        for (int p = 0; p <= url.Length; p++)
            if (MatchAt(pattern, url, p, requireEnd)) return true;
        return false;
    }

    /// <summary>Glob match: `*` = any run, `^` = separator (non-alnum, non `_-.%`) or end of URL.</summary>
    private static bool MatchAt(string pattern, string url, int start, bool requireEnd)
    {
        int pi = 0, ui = start, starP = -1, starU = -1;
        while (true)
        {
            if (pi == pattern.Length) { if (!requireEnd || ui == url.Length) return true; }
            else if (pattern[pi] == '*') { starP = pi++; starU = ui; continue; }
            else if (ui < url.Length && CharMatches(pattern[pi], url[ui])) { pi++; ui++; continue; }
            else if (pattern[pi] == '^' && ui == url.Length) { pi++; continue; }
            if (starP < 0) return false;
            pi = starP + 1; ui = ++starU;
            if (ui > url.Length) return false;
        }
    }

    private static bool CharMatches(char p, char u) =>
        p == '^' ? !(char.IsAsciiLetterOrDigit(u) || u is '_' or '-' or '.' or '%') : p == u;

    /// <summary>Micro-benchmark helper used by tests and the perf CI: p50/p95 lookup latency in microseconds.</summary>
    public (double P50Us, double P95Us) Benchmark(IReadOnlyList<NetworkRequest> requests, int rounds = 20)
    {
        var samples = new List<double>(requests.Count * rounds);
        var sw = new Stopwatch();
        for (int r = 0; r < rounds; r++)
            foreach (var q in requests)
            {
                sw.Restart(); Evaluate(q); sw.Stop();
                samples.Add(sw.Elapsed.TotalMicroseconds);
            }
        samples.Sort();
        return (samples[samples.Count / 2], samples[(int)(samples.Count * 0.95)]);
    }
}
