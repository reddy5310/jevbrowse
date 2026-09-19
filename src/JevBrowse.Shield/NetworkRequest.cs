namespace JevBrowse.Shield;

/// <summary>Resource types as WebView2 reports them, reduced to what filter options can name.</summary>
public enum RequestType { Document, SubDocument, Script, Stylesheet, Image, Font, Media, XmlHttpRequest, WebSocket, Ping, Other }

public sealed record NetworkRequest(Uri Url, Uri? Initiator, RequestType Type)
{
    public string Host => Url.Host.ToLowerInvariant();
    public bool IsThirdParty => Initiator is not null && !SiteOf(Url.Host).Equals(SiteOf(Initiator.Host), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Registrable domain (eTLD+1) using a small built-in list of multi-label public suffixes. V1 trade-off: the full
    /// Public Suffix List is ~250 KB and updates monthly; this covers the common cases and errs toward first-party.
    /// </summary>
    public static string SiteOf(string host)
    {
        host = host.ToLowerInvariant().TrimEnd('.');
        var labels = host.Split('.');
        if (labels.Length <= 2) return host;
        var last2 = labels[^2] + "." + labels[^1];
        int keep = MultiLabelSuffixes.Contains(last2) ? 3 : 2;
        return string.Join('.', labels[^Math.Min(keep, labels.Length)..]);
    }

    private static readonly HashSet<string> MultiLabelSuffixes = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "co.in", "net.in", "org.in", "co.jp", "ne.jp", "or.jp", "com.au", "net.au", "org.au",
        "com.br", "com.cn", "com.mx", "co.nz", "co.za", "com.sg", "com.tr", "com.ar", "co.kr", "com.hk", "github.io", "gitlab.io",
        "blogspot.com", "cloudfront.net", "amazonaws.com", "azurewebsites.net", "herokuapp.com", "vercel.app", "netlify.app", "pages.dev",
    };
}

public enum Verdict { Allow, Block }

/// <summary>A decision plus the rule that produced it, so the shield panel can show and disable it (§9.1).</summary>
public sealed record NetworkDecision(Verdict Verdict, string? Rule)
{
    public static readonly NetworkDecision Allowed = new(Verdict.Allow, null);
}
