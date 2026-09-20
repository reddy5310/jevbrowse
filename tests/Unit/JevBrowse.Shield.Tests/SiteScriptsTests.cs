using JevBrowse.Shield;

namespace JevBrowse.Shield.Tests;

public class SiteScriptsTests
{
    [Theory]
    [InlineData("www.youtube.com", true)]
    [InlineData("m.youtube.com", true)]
    [InlineData("youtube.com", true)]
    [InlineData("www.youtube-nocookie.com", true)]
    [InlineData("notyoutube.com", false)]
    [InlineData("youtube.com.evil.test", false)]
    public void Youtube_module_matches_only_youtube_hosts(string host, bool expected) =>
        Assert.Equal(expected, SiteScripts.For(host).Any(s => s.Name == "youtube"));

    [Fact]
    public void Scripts_only_use_the_fixed_bridge_strings()
    {
        var allowed = new[] { "jev:yt-ad-pruned", "jev:yt-ad-skipped", "jev:yt-wall" };
        foreach (var s in SiteScripts.All)
        {
            var posts = System.Text.RegularExpressions.Regex.Matches(s.Script, @"post\('([^']+)'\)").Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.NotEmpty(posts);
            Assert.All(posts, p => Assert.Contains(p, allowed));
            // No network from modules: strip comments, then look for real calls.
            var code = string.Join("\n", s.Script.Split('\n').Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l[..i] : l; }));
            Assert.DoesNotMatch(@"\bfetch\s*\(", code);
            Assert.DoesNotContain("XMLHttpRequest", code);
            Assert.DoesNotContain("navigator.sendBeacon", code);
        }
    }
}
