using JevBrowse.Domain;

namespace JevBrowse.AgentGateway;

/// <summary>
/// The user-approved maximum for agent sessions. An agent may request a manifest; the effective manifest is the
/// request CLAMPED to this ceiling, so an agent can only ever narrow what the user granted, never widen it.
/// Holding the endpoint token is therefore not the same as holding authority.
/// </summary>
public sealed class AgentCeiling
{
    public sealed record Result(AgentManifest Effective, IReadOnlyList<string> Adjustments);

    /// <summary>Agents may only ever get a throwaway identity. Personal/Work/Dev cookies are not grantable through this API.</summary>
    public static readonly IdentityContainer[] GrantableContainers = [IdentityContainer.Disposable, IdentityContainer.Private];

    public required AgentManifest Limits { get; init; }

    public Result Clamp(AgentManifest requested)
    {
        var adj = new List<string>();
        var m = new AgentManifest { Agent = string.IsNullOrWhiteSpace(requested.Agent) ? "agent" : requested.Agent, Workspace = requested.Workspace };

        // Domains: keep only those covered by an approved domain (equal or subdomain of one).
        foreach (var d in requested.AllowDomains.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct())
        {
            if (Limits.AllowDomains.Any(l => d == l.ToLowerInvariant() || d.EndsWith("." + l.ToLowerInvariant(), StringComparison.Ordinal))) m.AllowDomains.Add(d);
            else adj.Add($"domain '{d}' not approved: dropped");
        }

        // Actions: intersection.
        m.Actions = requested.Actions.Where(Limits.Actions.Contains).Distinct().ToList();
        foreach (var a in requested.Actions.Except(m.Actions)) adj.Add($"action {a} not approved: dropped");

        // Denied data classes: union (an agent cannot remove a deny).
        m.DenyDataClasses = requested.DenyDataClasses.Union(Limits.DenyDataClasses).Distinct().ToList();
        if (Limits.DenyDataClasses.Except(requested.DenyDataClasses).Any()) adj.Add("data-class denies merged with the approved ceiling");
        // A password/payment page is never exposed regardless (hard rule enforced in the gateway), so it is always denied.
        if (!m.DenyDataClasses.Contains(DataClass.Secret)) m.DenyDataClasses.Add(DataClass.Secret);

        // Destructive actions: the stricter of the two. "allow" needs the ceiling AND the request to say allow.
        m.DestructiveActions = Strictness(requested.DestructiveActions) >= Strictness(Limits.DestructiveActions) ? Normalize(requested.DestructiveActions) : Normalize(Limits.DestructiveActions);
        if (Normalize(requested.DestructiveActions) == "allow" && m.DestructiveActions != "allow") adj.Add("destructive 'allow' downgraded to '" + m.DestructiveActions + "'");

        // Budgets: the smaller of the two, never below 1.
        m.MaxLivePages = Math.Max(1, Math.Min(requested.MaxLivePages, Limits.MaxLivePages));
        m.SessionMinutes = Math.Max(1, Math.Min(requested.SessionMinutes, Limits.SessionMinutes));
        m.MaxActions = Math.Max(1, Math.Min(requested.MaxActions, Limits.MaxActions));
        m.MaxScreenshots = Math.Max(0, Math.Min(requested.MaxScreenshots, Limits.MaxScreenshots));
        if (m.MaxLivePages != requested.MaxLivePages || m.SessionMinutes != requested.SessionMinutes || m.MaxActions != requested.MaxActions || m.MaxScreenshots != requested.MaxScreenshots) adj.Add("budgets reduced to the approved ceiling");

        // Identity: only throwaway containers, and always a fresh workspace (the gateway ignores the requested name for lookup).
        m.Container = GrantableContainers.Contains(requested.Container) && GrantableContainers.Contains(Limits.Container) ? requested.Container : IdentityContainer.Disposable;
        if (m.Container != requested.Container) adj.Add($"container {requested.Container} not grantable to agents: {m.Container}");
        return new Result(m, adj);
    }

    private static string Normalize(string s) => s?.ToLowerInvariant() is "allow" or "deny" ? s.ToLowerInvariant() : "confirm";
    private static int Strictness(string s) => Normalize(s) switch { "deny" => 2, "confirm" => 1, _ => 0 };
}
