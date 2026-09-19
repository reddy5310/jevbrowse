using JevBrowse.Shield;

namespace JevBrowse.Shield.Tests;

public class CosmeticEngineTests
{
    [Fact]
    public void Generic_and_domain_scoped_rules_with_exceptions()
    {
        var e = CosmeticEngine.Compile(
        [
            "##.ad-slot",
            "##div[id^=\"google_ads\"]",
            "goodreturns.in,oneindia.com##.gr-ad-box",
            "example.com#@#.ad-slot",            // exception on example.com
            "~news.example.com##.sponsored",     // generic except news.example.com
            "! comment", "||net.rule^",          // ignored here
            "example.com#?#.x:has(> .y)",        // procedural: skipped
            "##.x:-abp-contains(ad)",            // skipped
        ]);
        Assert.Equal(3, e.GenericCount);         // .ad-slot, div[id^=google_ads], .sponsored
        Assert.Equal(2, e.Skipped);

        var gr = e.StylesheetFor("www.goodreturns.in");
        Assert.Contains(".gr-ad-box{display:none!important;}", gr);
        Assert.Contains(".ad-slot{display:none!important;}", gr);

        var ex = e.StylesheetFor("example.com");
        Assert.DoesNotContain(".ad-slot{", ex);
        Assert.Contains(".sponsored{", ex);

        var news = e.StylesheetFor("news.example.com");
        Assert.DoesNotContain(".sponsored{", news);
        Assert.DoesNotContain(".gr-ad-box{", news);

        Assert.DoesNotContain(".gr-ad-box{", e.StylesheetFor("unrelated.org"));
    }

    [Fact]
    public void One_rule_per_line_and_generic_sheet_is_cached()
    {
        var e = CosmeticEngine.Compile(["##.a", "##.b"]);
        var s1 = e.StylesheetFor("x.test");
        var s2 = e.StylesheetFor("y.test");
        Assert.Same(s1, s2);
        Assert.Equal(2, s1.Count(c => c == '\n'));
    }

    [Fact]
    public void Compiles_real_list_shape_quickly()
    {
        var lines = Enumerable.Range(0, 30_000).Select(i => i % 3 == 0 ? $"##.ad-{i}" : $"site{i % 500}.test##.slot-{i}").ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var e = CosmeticEngine.Compile(lines);
        var css = e.StylesheetFor("site7.test");
        sw.Stop();
        Assert.Equal(10_000, e.GenericCount);
        Assert.True(css.Length > 100_000);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");
    }
}
