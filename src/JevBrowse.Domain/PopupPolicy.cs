namespace JevBrowse.Domain;

public sealed record PopupDecision(bool Allow, string Reason);

/// <summary>
/// What to do when a page asks for a new window. There is no unmanaged popup: an allowed request becomes an ordinary managed tab (same identity as its opener,
/// through the same admission, Shield and permission setup as any tab); everything else is refused. Refusing is the default.
/// </summary>
public static class PopupPolicy
{
    public static PopupDecision Decide(bool userInitiated, bool openerIsAgentPage, bool openerIsInFront, string? scheme)
    {
        if (openerIsAgentPage) return new(false, "an agent's page may not open windows");
        if (!userInitiated) return new(false, "the page opened it without a click or key press");
        if (!openerIsInFront) return new(false, "it came from a page you are not looking at");
        if (scheme is not ("http" or "https")) return new(false, "it was not a web address");
        return new(true, "opened as a new tab");
    }
}
