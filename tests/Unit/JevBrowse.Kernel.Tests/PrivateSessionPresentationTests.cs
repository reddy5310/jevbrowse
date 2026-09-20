using JevBrowse.Domain;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

/// <summary>The four cases the private-session flow was specified against, as behaviour of the presentation rules.</summary>
public class PrivateSessionPresentationTests
{
    private static readonly Workspace Personal = new(ContextId.Default, "Personal");
    private static readonly Workspace Work = new(ContextId.New(), "Work");
    private static readonly Workspace Mine = new(ContextId.New(), "Private") { Container = IdentityContainer.Private };
    private static readonly Workspace AgentPrivate = new(ContextId.New(), "Agent · 1a2b") { Container = IdentityContainer.Private };

    private static PrivateSessionView Describe(ContextId active, ContextId? mine, ContextId returnTo, int tabs = 2, int pending = 0) =>
        PrivateSessionPresentation.Describe([Personal, Work, Mine, AgentPrivate], active, mine, returnTo, tabs, pending);

    [Fact]
    public void Return_names_the_workspace_the_user_actually_came_from()
    {
        // Entered from Work, so "Return to Personal" would be false.
        var v = Describe(active: Mine.Id, mine: Mine.Id, returnTo: Work.Id);
        Assert.True(v.ShowReturn);
        Assert.Equal("Return to Work", v.ReturnLabel);
    }

    [Fact]
    public void An_agents_private_workspace_is_never_treated_as_the_users_session()
    {
        // Agents may be granted a Private workspace and activating their tab makes it the active one. The user's End
        // button must not appear to act on it, and it must not read as "you are in your private session".
        var v = Describe(active: AgentPrivate.Id, mine: Mine.Id, returnTo: Personal.Id);
        Assert.False(v.InSession);
        Assert.False(v.ShowReturn);
        Assert.DoesNotContain("You are in your private session", v.Text);

        var noSession = Describe(active: AgentPrivate.Id, mine: null, returnTo: Personal.Id);
        Assert.False(noSession.HasSession);
        Assert.False(noSession.ShowEnd);          // an agent's Private workspace alone is not something to end here
    }

    [Fact]
    public void A_session_left_in_the_background_can_still_be_ended_and_says_it_is_open()
    {
        var v = Describe(active: Work.Id, mine: Mine.Id, returnTo: Work.Id, tabs: 3);
        Assert.True(v.HasSession);
        Assert.False(v.InSession);
        Assert.True(v.ShowEnd);
        Assert.Equal("End private session", v.EndLabel);
        Assert.Contains("still open in the background (3 tabs)", v.Text);
        Assert.Contains("cookies", v.Text);
    }

    [Fact]
    public void A_new_session_does_not_hide_an_earlier_unfinished_cleanup()
    {
        var v = Describe(active: Work.Id, mine: Mine.Id, returnTo: Work.Id, tabs: 1, pending: 1);
        Assert.Contains("still open in the background (1 tab)", v.Text);      // the new session, singular
        Assert.Contains("earlier private session's temporary data has not been deleted", v.Text);
        Assert.True(v.ShowEnd);
        Assert.Equal("End private session", v.EndLabel);                       // and ending it retries the earlier one
    }

    [Fact]
    public void After_ending_only_an_unfinished_cleanup_keeps_a_control_visible()
    {
        var gone = Describe(active: Personal.Id, mine: null, returnTo: Personal.Id, pending: 0);
        Assert.False(gone.ShowEnd);

        // Renderers closed but profile files not yet deleted: the state must be retryable, not silently "done".
        var pending = Describe(active: Personal.Id, mine: null, returnTo: Personal.Id, pending: 1);
        Assert.True(pending.ShowEnd);
        Assert.Equal("Retry private cleanup", pending.EndLabel);
        Assert.Contains("incomplete", pending.Text);
    }
}
