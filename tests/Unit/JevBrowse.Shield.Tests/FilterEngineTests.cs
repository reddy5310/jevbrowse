using JevBrowse.Shield;

namespace JevBrowse.Shield.Tests;

public class FilterEngineTests
{
    private static NetworkRequest Req(string url, string? initiator = "https://news.example.com/story", RequestType type = RequestType.Script) =>
        new(new Uri(url), initiator is null ? null : new Uri(initiator), type);

    private static FilterEngine Engine(params string[] lines) => FilterEngine.Compile(lines);

    [Theory]
    [InlineData("||ads.example.net^", "https://ads.example.net/x.js", true)]
    [InlineData("||ads.example.net^", "https://sub.ads.example.net/x.js", true)]
    [InlineData("||ads.example.net^", "https://notads.example.net/x.js", false)]
    [InlineData("||ads.example.net^", "https://ads.example.network/x.js", false)]
    [InlineData("/banner/*/ad.", "https://cdn.site.com/banner/300x250/ad.png", true)]
    [InlineData("/banner/*/ad.", "https://cdn.site.com/banner/ad.png", false)]
    [InlineData("|https://tracker.", "https://tracker.io/p", true)]
    [InlineData("|https://tracker.", "https://www.tracker.io/p", false)]
    [InlineData("-analytics.js|", "https://x.com/site-analytics.js", true)]
    [InlineData("-analytics.js|", "https://x.com/site-analytics.js?v=2", false)]
    [InlineData("||example.com/api^", "https://example.com/api?x=1", true)]
    [InlineData("||example.com/api^", "https://example.com/api", true)]
    [InlineData("||example.com/api^", "https://example.com/apiary", false)]
    public void Pattern_matching(string rule, string url, bool blocked)
    {
        var d = Engine(rule).Evaluate(Req(url));
        Assert.Equal(blocked ? Verdict.Block : Verdict.Allow, d.Verdict);
    }

    [Fact]
    public void Exceptions_override_blocks_and_are_reported()
    {
        var e = Engine("||cdn.example.com^", "@@||cdn.example.com/fonts/");
        Assert.Equal(Verdict.Block, e.Evaluate(Req("https://cdn.example.com/ads.js")).Verdict);
        var d = e.Evaluate(Req("https://cdn.example.com/fonts/a.woff"));
        Assert.Equal(Verdict.Allow, d.Verdict);
        Assert.StartsWith("@@", d.Rule);
    }

    [Fact]
    public void Third_party_option()
    {
        var e = Engine("||widgets.example.com^$third-party");
        Assert.Equal(Verdict.Block, e.Evaluate(Req("https://widgets.example.com/w.js", "https://other.org/")).Verdict);
        Assert.Equal(Verdict.Allow, e.Evaluate(Req("https://widgets.example.com/w.js", "https://www.example.com/")).Verdict);
        Assert.Equal(Verdict.Allow, e.Evaluate(Req("https://widgets.example.com/w.js", null)).Verdict); // no initiator = first-party
    }

    [Theory]
    [InlineData("a.b.co.uk", "b.co.uk")]
    [InlineData("www.example.com", "example.com")]
    [InlineData("deep.sub.example.com", "example.com")]
    [InlineData("user.github.io", "user.github.io")]
    [InlineData("localhost", "localhost")]
    public void Registrable_domain(string host, string site) => Assert.Equal(site, NetworkRequest.SiteOf(host));

    [Fact]
    public void Resource_type_options()
    {
        var e = Engine("||media.example.com^$image,media");
        Assert.Equal(Verdict.Block, e.Evaluate(Req("https://media.example.com/a.png", type: RequestType.Image)).Verdict);
        Assert.Equal(Verdict.Allow, e.Evaluate(Req("https://media.example.com/a.js", type: RequestType.Script)).Verdict);
        var neg = Engine("||cdn.example.com^$~stylesheet");
        Assert.Equal(Verdict.Allow, neg.Evaluate(Req("https://cdn.example.com/a.css", type: RequestType.Stylesheet)).Verdict);
        Assert.Equal(Verdict.Block, neg.Evaluate(Req("https://cdn.example.com/a.js")).Verdict);
    }

    [Fact]
    public void Domain_option_scopes_by_initiator_site()
    {
        var e = Engine("||stats.example.com^$domain=news.example.com|~sports.news.example.com");
        Assert.Equal(Verdict.Block, e.Evaluate(Req("https://stats.example.com/s", "https://news.example.com/a")).Verdict);
        Assert.Equal(Verdict.Allow, e.Evaluate(Req("https://stats.example.com/s", "https://sports.news.example.com/a")).Verdict);
        Assert.Equal(Verdict.Allow, e.Evaluate(Req("https://stats.example.com/s", "https://other.com/a")).Verdict);
    }

    [Fact]
    public void Unsupported_lines_are_skipped_not_misapplied()
    {
        var e = Engine("! comment", "[Adblock Plus 2.0]", "example.com##.ad", "/regex.*rule/", "||ok.com^$unknownopt", "||real.com^");
        Assert.Equal(1, e.RuleCount);
        Assert.Equal(5, e.SkippedLines);
        Assert.Equal(Verdict.Allow, e.Evaluate(Req("https://ok.com/x")).Verdict);
    }

    [Fact]
    public void Unknown_is_allowed()
    {
        var e = Engine("||ads.example.net^");
        Assert.Equal(NetworkDecision.Allowed, e.Evaluate(Req("https://totally.fine.org/app.js")));
    }

    [Fact]
    public void Lookup_stays_in_microseconds_with_a_large_list()
    {
        // Synthetic list shaped like EasyList: 40k domain rules + 10k path rules + 2k exceptions.
        var rng = new Random(7);
        var lines = new List<string>();
        for (int i = 0; i < 40_000; i++) lines.Add($"||ad{i}.tracker{rng.Next(2000)}.net^");
        for (int i = 0; i < 10_000; i++) lines.Add($"/adpath{i}/*.js");
        for (int i = 0; i < 2_000; i++) lines.Add($"@@||ad{i}.tracker{rng.Next(2000)}.net/allowed/");
        var e = FilterEngine.Compile(lines);
        Assert.Equal(52_000, e.RuleCount);

        var reqs = new List<NetworkRequest>();
        for (int i = 0; i < 300; i++) reqs.Add(Req($"https://cdn{i}.example.org/static/app-{i}.js?v={i}"));
        for (int i = 0; i < 100; i++) reqs.Add(Req($"https://ad{i * 7}.tracker{i}.net/pixel.gif", type: RequestType.Image));
        var (p50, p95) = e.Benchmark(reqs);
        Assert.True(p95 < 500, $"p95 {p95:F1} µs, p50 {p50:F1} µs");
    }
}
