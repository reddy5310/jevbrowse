using System.Text.Json;
using System.Text.Json.Serialization;
using JevBrowse.Domain;

namespace JevBrowse.DevSpace;

/// <summary>Table A.9 environment spaces. Unknown is a first-class answer: DevSpace never guesses PROD (§13.1).</summary>
public enum DeployEnvironment { Unknown, Local, Dev, Staging, Prod }

/// <summary>One explicit rule: a host glob (and optional port) maps to an environment.</summary>
public sealed record EnvironmentRule(string HostPattern, int? Port, DeployEnvironment Environment)
{
    public bool Matches(Uri u)
    {
        if (Port is { } p && u.Port != p) return false;
        return Glob(HostPattern, u.Host);
    }

    internal static bool Glob(string pattern, string host)
    {
        pattern = pattern.ToLowerInvariant(); host = host.ToLowerInvariant();
        if (pattern == host) return true;
        if (pattern.StartsWith("*.")) return host.EndsWith(pattern[1..], StringComparison.Ordinal) || host == pattern[2..];
        if (pattern.EndsWith(".*")) return host.StartsWith(pattern[..^1], StringComparison.Ordinal);
        return false;
    }
}

/// <summary>A project the user configured: rules, localhost services, and destructive-action confirmations.</summary>
public sealed class ProjectConfig
{
    public string Name { get; set; } = "";
    public List<EnvironmentRule> Rules { get; set; } = [];
    /// <summary>Local services to probe on the dashboard, e.g. "api:5000", "web:3000".</summary>
    public List<string> LocalServices { get; set; } = [];
    /// <summary>URL path substrings that trigger a confirmation in PROD, e.g. "/delete", "/deploy". Explicit list only.</summary>
    public List<string> ConfirmInProd { get; set; } = [];
    public string? WorkspaceId { get; set; }
}

public sealed record EnvironmentResolution(DeployEnvironment Environment, string Reason, ProjectConfig? Project);

/// <summary>
/// Explicit configuration first; heuristics only ever produce LOCAL (loopback/.local). Anything else is Unknown,
/// never a guessed PROD (§13.1 "never rely solely on AI guesses").
/// </summary>
public sealed class EnvironmentResolver
{
    private readonly IReadOnlyList<ProjectConfig> _projects;
    public EnvironmentResolver(IReadOnlyList<ProjectConfig> projects) => _projects = projects;

    public EnvironmentResolution Resolve(Uri url)
    {
        foreach (var p in _projects)
            foreach (var r in p.Rules)
                if (r.Matches(url)) return new(r.Environment, $"project '{p.Name}' rule {r.HostPattern}{(r.Port is { } port ? ":" + port : "")}", p);

        if (url.IsLoopback || url.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return new(DeployEnvironment.Local, "loopback / .local host", null);
        return new(DeployEnvironment.Unknown, "no project rule matches; DevSpace does not guess", null);
    }

    /// <summary>Production Safety Guard: only for explicitly configured destructive paths in an explicitly PROD environment.</summary>
    public bool ShouldConfirm(Uri url, out string why)
    {
        why = "";
        var r = Resolve(url);
        if (r.Environment != DeployEnvironment.Prod || r.Project is null) return false;
        var path = url.PathAndQuery.ToLowerInvariant();
        var hit = r.Project.ConfirmInProd.FirstOrDefault(s => path.Contains(s.ToLowerInvariant()));
        if (hit is null) return false;
        why = $"PROD ({r.Reason}) and path matches configured '{hit}'";
        return true;
    }
}

/// <summary>User-editable JSON at &lt;data&gt;/devspace/projects.json. No magic: the file is the configuration.</summary>
public static class ProjectStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() }, PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<ProjectConfig> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<ProjectConfig>>(File.ReadAllText(path), Opts) ?? []; }
        catch (JsonException) { return []; }
    }

    public static void Save(string path, IReadOnlyList<ProjectConfig> projects)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(projects, Opts));
        File.Move(tmp, path, overwrite: true);
    }

    public static ProjectConfig Example() => new()
    {
        Name = "JevBrowse",
        Rules =
        [
            new("localhost", 3000, DeployEnvironment.Local),
            new("*.dev.example.com", null, DeployEnvironment.Dev),
            new("staging.example.com", null, DeployEnvironment.Staging),
            new("example.com", null, DeployEnvironment.Prod),
            new("*.example.com", null, DeployEnvironment.Prod),
        ],
        LocalServices = ["web:3000", "api:5000"],
        ConfirmInProd = ["/delete", "/deploy", "/settings/danger"],
    };
}
