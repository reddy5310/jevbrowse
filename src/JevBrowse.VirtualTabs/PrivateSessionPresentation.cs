using JevBrowse.Domain;

namespace JevBrowse.VirtualTabs;

/// <summary>What the shell should show about the user's private session, decided from facts rather than from the UI.</summary>
public sealed record PrivateSessionView(
    bool HasSession, bool InSession, bool ShowEnd, bool ShowReturn, string EndLabel, string ReturnLabel, string Text);

/// <summary>
/// Pure presentation rules for the private session, kept out of the window so the rules that matter can be tested.
///
/// Ownership is by IDENTITY of the session the user started, never by container type. Agents can also be granted a
/// Private workspace, and activating an agent tab makes that workspace the active one; a rule of "the active
/// workspace is Private, so this is the user's session" would show the user's End button on an agent's session and
/// end it. The user's session is exactly the workspace the shell created for them.
/// </summary>
public static class PrivateSessionPresentation
{
    public static PrivateSessionView Describe(
        IReadOnlyList<Workspace> workspaces, ContextId activeWorkspace, ContextId? mySession,
        ContextId returnTo, int tabsInSession, int cleanupPending)
    {
        var exists = mySession is { } m && workspaces.Any(w => w.Id == m);
        var inside = exists && activeWorkspace == mySession;
        var returnName = workspaces.FirstOrDefault(w => w.Id == returnTo)?.Name ?? "your workspace";

        // The end control exists whenever there is something to end, wherever the user currently is: a session left in
        // the background still holds its cookies, and hiding the only way to end it would make "switching away" a
        // way to forget it.
        var showEnd = exists || cleanupPending > 0;
        var tabs = tabsInSession == 1 ? "1 tab" : $"{tabsInSession} tabs";

        // Two independent facts, each stated when true. An earlier session whose files could not be deleted stays
        // visible however many new sessions have been started since: hiding it behind a fresh one would make "retry"
        // disappear exactly when the user has most reason to think everything is clean.
        var session = inside ? $"You are in your private session ({tabs}). Returning keeps it open; ending it closes it."
            : exists ? $"Your private session is still open in the background ({tabs}) and keeps its cookies until you end it."
            : "";
        var cleanup = cleanupPending == 0 ? ""
            : exists ? "An earlier private session's temporary data has not been deleted yet. Ending this session will try that again too."
            : "Private cleanup is incomplete. Retry to check again.";
        var text = string.Join(" ", new[] { session, cleanup }.Where(t => t.Length > 0));

        return new PrivateSessionView(
            HasSession: exists,
            InSession: inside,
            ShowEnd: showEnd,
            ShowReturn: inside,
            EndLabel: exists ? "End private session" : "Retry private cleanup",
            ReturnLabel: $"Return to {returnName}",
            Text: text);
    }
}
