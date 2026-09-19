using System.Text.RegularExpressions;

namespace JevBrowse.DevSpace;

public sealed record ConsoleEntry(DateTimeOffset At, string Level, string Message, string? Source, int? Line);

public sealed record ErrorGroup(string Signature, int Count, ConsoleEntry First, ConsoleEntry Last, IReadOnlyList<ConsoleEntry> Cascade)
{
    public string Headline => First.Message.Length > 140 ? First.Message[..140] + "…" : First.Message;
}

/// <summary>
/// Error intelligence (Table A.9): group repeated errors by a normalized signature, and attach the errors that
/// followed within a short window as a probable cascade, so the *first* error in a burst is what gets explained.
/// Measured grouping first; AI explanation is a separate, explicit step.
/// </summary>
public static partial class ErrorGrouper
{
    public static IReadOnlyList<ErrorGroup> Group(IReadOnlyList<ConsoleEntry> entries, TimeSpan? cascadeWindow = null)
    {
        var window = cascadeWindow ?? TimeSpan.FromMilliseconds(500);
        var errors = entries.Where(e => e.Level is "error" or "exception").OrderBy(e => e.At).ToList();
        var groups = new Dictionary<string, (List<ConsoleEntry> items, List<ConsoleEntry> cascade)>();
        ConsoleEntry? burstRoot = null;
        foreach (var e in errors)
        {
            var sig = Signature(e.Message);
            if (!groups.TryGetValue(sig, out var g)) groups[sig] = g = ([], []);
            g.items.Add(e);
            if (burstRoot is not null && e.At - burstRoot.At <= window && Signature(burstRoot.Message) != sig)
                groups[Signature(burstRoot.Message)].cascade.Add(e);
            else
                burstRoot = e;
        }
        return groups
            .Select(kv => new ErrorGroup(kv.Key, kv.Value.items.Count, kv.Value.items[0], kv.Value.items[^1], kv.Value.cascade))
            .OrderByDescending(g => g.Cascade.Count).ThenByDescending(g => g.Count).ThenBy(g => g.First.At)
            .ToList();
    }

    /// <summary>Strip volatile parts (numbers, urls, hex ids, quoted values) so "x 404 at /a/1" and "x 404 at /a/2" group.</summary>
    public static string Signature(string message)
    {
        var s = Urls().Replace(message, "<url>");
        s = Hex().Replace(s, "<id>");
        s = Quoted().Replace(s, "<q>");
        s = Numbers().Replace(s, "<n>");
        return s.Trim().ToLowerInvariant();
    }

    [GeneratedRegex(@"https?://\S+")] private static partial Regex Urls();
    [GeneratedRegex(@"\b[0-9a-f]{8,}\b", RegexOptions.IgnoreCase)] private static partial Regex Hex();
    [GeneratedRegex("\"[^\"]*\"|'[^']*'")] private static partial Regex Quoted();
    [GeneratedRegex(@"\d+")] private static partial Regex Numbers();
}

public enum NetKind { Api, Static, ThirdParty, Failed, Slow, Duplicate }

public sealed record NetEntry(DateTimeOffset At, Uri Url, string Method, int Status, TimeSpan? Duration, string Initiator);

public sealed record NetSummary(IReadOnlyDictionary<NetKind, int> Counts, IReadOnlyList<NetEntry> Failed, IReadOnlyList<NetEntry> Slow, IReadOnlyList<(Uri Url, int Times)> Duplicates, int Total, long? Transferred);

/// <summary>Network intelligence (Table A.9): buckets a page's requests into what a developer scans for first.</summary>
public static class NetworkGrouper
{
    private static readonly string[] StaticExt = [".js", ".css", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".woff", ".woff2", ".ttf", ".ico", ".map"];

    public static NetSummary Summarize(IReadOnlyList<NetEntry> entries, string pageHost, TimeSpan? slowAfter = null)
    {
        var slow = slowAfter ?? TimeSpan.FromMilliseconds(1500);
        var counts = Enum.GetValues<NetKind>().ToDictionary(k => k, _ => 0);
        var site = SiteOf(pageHost);
        foreach (var e in entries)
        {
            var path = e.Url.AbsolutePath.ToLowerInvariant();
            bool isStatic = StaticExt.Any(x => path.EndsWith(x, StringComparison.Ordinal));
            bool third = SiteOf(e.Url.Host) != site;
            if (third) counts[NetKind.ThirdParty]++;
            else if (isStatic) counts[NetKind.Static]++;
            else counts[NetKind.Api]++;
            if (e.Status == 0 || e.Status >= 400) counts[NetKind.Failed]++;
            if (e.Duration is { } d && d >= slow) counts[NetKind.Slow]++;
        }
        var dupes = entries.Where(e => e.Method == "GET").GroupBy(e => e.Url).Where(g => g.Count() > 1).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
        counts[NetKind.Duplicate] = dupes.Sum(d => d.Item2 - 1);
        return new NetSummary(counts,
            entries.Where(e => e.Status == 0 || e.Status >= 400).ToList(),
            entries.Where(e => e.Duration is { } d && d >= slow).OrderByDescending(e => e.Duration).ToList(),
            dupes, entries.Count, null);
    }

    private static string SiteOf(string host)
    {
        var l = host.ToLowerInvariant().Split('.');
        return l.Length <= 2 ? host.ToLowerInvariant() : string.Join('.', l[^2..]);
    }
}

/// <summary>Localhost dashboard: TCP-connect probe of configured services. I/O lives here, not in the resolver.</summary>
public static class LocalServiceProbe
{
    public sealed record Result(string Name, int Port, bool Up, TimeSpan Latency);

    public static async Task<IReadOnlyList<Result>> ProbeAsync(IEnumerable<string> services, CancellationToken ct = default)
    {
        var tasks = services.Select(async s =>
        {
            var parts = s.Split(':');
            var name = parts[0];
            if (parts.Length < 2 || !int.TryParse(parts[1], out var port)) return new Result(name, 0, false, TimeSpan.Zero);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var connect = client.ConnectAsync("127.0.0.1", port, ct).AsTask();
                if (await Task.WhenAny(connect, Task.Delay(1500, ct)) != connect) return new Result(name, port, false, sw.Elapsed);
                await connect;
                return new Result(name, port, true, sw.Elapsed);
            }
            catch (Exception) { return new Result(name, port, false, sw.Elapsed); }
        });
        return await Task.WhenAll(tasks);
    }
}
