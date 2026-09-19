using JevBrowse.DevSpace;

namespace JevBrowse.DevSpace.Tests;

public class EnvironmentResolverTests
{
    private static readonly EnvironmentResolver R = new([ProjectStore.Example()]);

    [Theory]
    [InlineData("http://localhost:3000/", DeployEnvironment.Local)]
    [InlineData("https://api.dev.example.com/x", DeployEnvironment.Dev)]
    [InlineData("https://staging.example.com/", DeployEnvironment.Staging)]
    [InlineData("https://example.com/", DeployEnvironment.Prod)]
    [InlineData("https://www.example.com/", DeployEnvironment.Prod)]
    [InlineData("http://127.0.0.1:8080/", DeployEnvironment.Local)]
    [InlineData("http://myapp.local/", DeployEnvironment.Local)]
    [InlineData("https://github.com/", DeployEnvironment.Unknown)]
    [InlineData("https://prod.someone-else.com/", DeployEnvironment.Unknown)]
    public void Explicit_rules_then_loopback_then_unknown(string url, DeployEnvironment expected) =>
        Assert.Equal(expected, R.Resolve(new Uri(url)).Environment);

    [Fact]
    public void Never_guesses_prod_from_words_in_the_host()
    {
        var r = new EnvironmentResolver([]).Resolve(new Uri("https://prod-console.corp.com/"));
        Assert.Equal(DeployEnvironment.Unknown, r.Environment);
        Assert.Contains("does not guess", r.Reason);
    }

    [Fact]
    public void Port_rule_is_specific()
    {
        Assert.Equal("project 'JevBrowse' rule localhost:3000", R.Resolve(new Uri("http://localhost:3000/")).Reason);
        Assert.Equal("loopback / .local host", R.Resolve(new Uri("http://localhost:9999/")).Reason);
    }

    [Fact]
    public void Prod_confirmation_only_for_configured_paths_in_explicit_prod()
    {
        Assert.True(R.ShouldConfirm(new Uri("https://example.com/admin/delete?id=1"), out var why));
        Assert.Contains("/delete", why);
        Assert.False(R.ShouldConfirm(new Uri("https://example.com/read"), out _));
        Assert.False(R.ShouldConfirm(new Uri("https://staging.example.com/delete"), out _));   // not prod
        Assert.False(R.ShouldConfirm(new Uri("https://unknown.com/delete"), out _));           // unknown, never
    }

    [Fact]
    public void Project_store_roundtrips_and_tolerates_bad_json()
    {
        var path = Path.Combine(Path.GetTempPath(), "jev-devspace-" + Guid.NewGuid().ToString("N"), "projects.json");
        ProjectStore.Save(path, [ProjectStore.Example()]);
        var loaded = ProjectStore.Load(path);
        Assert.Single(loaded);
        Assert.Equal(5, loaded[0].Rules.Count);
        Assert.Equal(DeployEnvironment.Prod, loaded[0].Rules[3].Environment);
        File.WriteAllText(path, "{ not json");
        Assert.Empty(ProjectStore.Load(path));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}

public class ErrorGrouperTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static ConsoleEntry E(int ms, string msg, string level = "error") => new(T0.AddMilliseconds(ms), level, msg, "app.js", 10);

    [Fact]
    public void Groups_by_signature_ignoring_ids_urls_and_numbers()
    {
        var groups = ErrorGrouper.Group(
        [
            E(0, "GET https://api.test/users/123 404 (Not Found)"),
            E(10, "GET https://api.test/users/456 404 (Not Found)"),
            E(20, "GET https://api.test/users/789 404 (Not Found)"),
            E(3000, "Uncaught TypeError: Cannot read properties of undefined (reading 'name')"),
            E(3001, "warning only", "warning"),
        ]);
        Assert.Equal(2, groups.Count);
        Assert.Equal(3, groups.First(g => g.Headline.Contains("404")).Count);
    }

    [Fact]
    public void Cascade_attaches_followers_within_window_to_the_first_error()
    {
        var groups = ErrorGrouper.Group(
        [
            E(0, "Failed to load config.json"),
            E(50, "Uncaught TypeError: config is undefined"),
            E(80, "Uncaught TypeError: cannot render"),
            E(5000, "Unrelated later error"),
        ]);
        var root = groups[0];
        Assert.Contains("config.json", root.Headline);
        Assert.Equal(2, root.Cascade.Count);
        Assert.Empty(groups.First(g => g.Headline.Contains("Unrelated")).Cascade);
    }
}

public class NetworkGrouperTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static NetEntry N(string url, int status = 200, int ms = 100, string method = "GET") => new(T0, new Uri(url), method, status, TimeSpan.FromMilliseconds(ms), "");

    [Fact]
    public void Buckets_api_static_thirdparty_failed_slow_duplicate()
    {
        var s = NetworkGrouper.Summarize(
        [
            N("https://app.example.com/api/users"),
            N("https://app.example.com/api/orders", status: 500),
            N("https://app.example.com/static/app.js"),
            N("https://app.example.com/static/app.js"),        // duplicate
            N("https://cdn.thirdparty.net/lib.js"),
            N("https://app.example.com/api/slow", ms: 4000),
            N("https://app.example.com/api/timeout", status: 0),
        ], "app.example.com");
        Assert.Equal(4, s.Counts[NetKind.Api]);
        Assert.Equal(2, s.Counts[NetKind.Static]);
        Assert.Equal(1, s.Counts[NetKind.ThirdParty]);
        Assert.Equal(2, s.Counts[NetKind.Failed]);
        Assert.Equal(1, s.Counts[NetKind.Slow]);
        Assert.Equal(1, s.Counts[NetKind.Duplicate]);
        Assert.Equal("/api/slow", s.Slow[0].Url.AbsolutePath);
        Assert.Single(s.Duplicates);
    }
}

public class LocalServiceProbeTests
{
    [Fact]
    public async Task Detects_open_and_closed_ports()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var r = await LocalServiceProbe.ProbeAsync([$"up:{port}", "down:1", "bad"]);
            Assert.True(r.Single(x => x.Name == "up").Up);
            Assert.False(r.Single(x => x.Name == "down").Up);
            Assert.False(r.Single(x => x.Name == "bad").Up);
        }
        finally { listener.Stop(); }
    }
}
