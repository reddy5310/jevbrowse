namespace JevBrowse.AgentGateway;

/// <summary>What the panel needs to know about one agent session, as plain facts. Built from the live session by the shell.</summary>
public sealed record AgentSessionFacts(
    string Id, string Agent, bool CleanedUp, bool Closed, DateTimeOffset ExpiresAt, int ActionsUsed, int MaxActions,
    int LivePages, int MaxLivePages, IReadOnlyList<string> Actions, IReadOnlyList<string> Domains, IReadOnlyList<string> DeniedClasses,
    IReadOnlyList<AuditEntry> Audit);

/// <summary>One agent session, in the words a person reads to decide whether to let it carry on.</summary>
public sealed record AgentSessionCard(
    string Id, string Agent, string Status, string Summary, string Scope, IReadOnlyList<string> Recent, bool CanStop);

/// <summary>
/// The agent activity view: who is acting on the browser, what they may do, and what they just did, including what was
/// refused. It states only what the gateway recorded. "Stopped" is said only once the pages are actually released, so the
/// panel never claims more than has happened.
/// </summary>
public static class AgentActivity
{
    public static IReadOnlyList<AgentSessionCard> Build(IEnumerable<AgentSessionFacts> sessions, DateTimeOffset now, int recent = 8) =>
        sessions
            .OrderBy(s => s.CleanedUp).ThenByDescending(s => s.ExpiresAt)   // running ones first
            .Select(s => Card(s, now, recent)).ToList();

    private static AgentSessionCard Card(AgentSessionFacts s, DateTimeOffset now, int recent)
    {
        string status;
        if (s.CleanedUp) status = "Stopped. Its pages have been released.";
        else if (s.Closed) status = "Stopping. Its pages are not all released yet.";
        else if (now >= s.ExpiresAt) status = "Expired. It can no longer act.";
        else
        {
            var left = s.ExpiresAt - now;
            status = left.TotalMinutes >= 1 ? $"Running. About {Math.Ceiling(left.TotalMinutes):0} min left." : "Running. Under a minute left.";
        }
        var pages = s.LivePages == 1 ? "1 page open" : $"{s.LivePages} pages open";
        var summary = $"{s.ActionsUsed} of {s.MaxActions} actions used · {pages} (up to {s.MaxLivePages})";
        var scope = $"May: {Join(s.Actions, "nothing")}. On: {Join(s.Domains, "no sites")}. Never touches: {Join(s.DeniedClasses, "nothing excluded")} data.";
        var lines = s.Audit.OrderByDescending(a => a.At).Take(recent).Select(Line).ToList();
        return new AgentSessionCard(s.Id, s.Agent, status, summary, scope, lines, CanStop: !s.CleanedUp);
    }

    private static string Join(IReadOnlyList<string> items, string none) => items.Count == 0 ? none : string.Join(", ", items);

    private static string Line(AuditEntry a)
    {
        var target = a.Target.Length > 80 ? a.Target[..80] + "…" : a.Target;
        var what = string.IsNullOrWhiteSpace(target) ? a.Action : $"{a.Action} {target}";
        return a.Allowed ? $"{a.At.ToLocalTime():HH:mm:ss}  {what}: done" : $"{a.At.ToLocalTime():HH:mm:ss}  {what}: refused, {RefusalWords(a.Reason)}";
    }

    /// <summary>The gateway's reason codes, in a sentence a person can act on. An unknown code is shown as it is rather than guessed at.</summary>
    public static string RefusalWords(string reason)
    {
        // "hard:" marks a rule that no setting can relax; the whole string is the code, not a code with a detail.
        var (code, detail) = !reason.StartsWith("hard:", StringComparison.Ordinal) && reason.Split(':', 2) is { Length: 2 } p ? (p[0], p[1]) : (reason, "");
        return code switch
        {
            "session_closed" => "the session had already been stopped",
            "session_revoked" => "the session was stopped",
            "session_expired" => "the session ran out of time",
            "action_quota_exhausted" => "it had used all the actions it was allowed",
            "action_not_granted" => $"you did not allow it to {detail.ToLowerInvariant()}",
            "bad_url" => "that was not a web address",
            "domain_not_allowed" => $"{detail} is not on the approved list",
            "live_page_quota_unsatisfiable" => "it would have needed more open pages than allowed",
            "hard:secret_on_screen" => "the page shows a password or payment field, so a picture of it is never taken",
            "screenshots_not_approved" => "screenshots are experimental and the person did not approve them for this session",
            "screenshot_quota_exhausted" => "it had taken all the pictures it was allowed",
            "screenshot_discarded" => "the page changed while the picture was being taken, so the picture was thrown away",
            "screenshot_too_large" => "the picture was larger than the limit, so it was not sent",
            "screenshot_not_an_image" => "the browser did not produce a valid picture",
            "renderer_pool_full" => "the browser had no spare capacity, and it will not close the page you are reading to make room",
            "no_current_page" or "renderer_unavailable" => "there was no page open for it to use",
            "hard:secret_page" => "the page holds secrets, which an agent never touches",
            "data_class_denied" => $"the page is {detail.ToLowerInvariant()}, which you kept off limits",
            "destructive_denied_by_manifest" => "it tried something that changes things, which was not allowed",
            "destructive_not_confirmed_by_user" => "a step that changes things was not confirmed by you",
            "hard:secret_field_selector" => "it tried to type into a password or secret field",
            "missing_selector" or "missing_selector_or_text" => "the request was incomplete",
            "unknown_action" => "that is not something it can do",
            _ => reason,
        };
    }
}
