using JevBrowse.AgentGateway;
using Xunit;

namespace JevBrowse.AgentGateway.Tests;

public class AgentActivityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static AgentSessionFacts Session(string agent = "Claude Code", bool cleaned = false, bool closed = false, int minutesLeft = 30,
        IReadOnlyList<AuditEntry>? audit = null, int used = 12) =>
        new("abc123", agent, cleaned, closed, Now.AddMinutes(minutesLeft), used, 200, 2, 3, ["Navigate", "Read"], ["github.com", "localhost"],
            ["Authenticated", "Sensitive", "Secret"], audit ?? []);

    [Fact]
    public void A_running_session_says_who_how_long_and_what_it_may_do()
    {
        var c = AgentActivity.Build([Session()], Now).Single();
        Assert.Equal("Running. About 30 min left.", c.Status);
        Assert.Equal("12 of 200 actions used · 2 pages open (up to 3)", c.Summary);
        Assert.Equal("May: Navigate, Read. On: github.com, localhost. Never touches: Authenticated, Sensitive, Secret data.", c.Scope);
        Assert.True(c.CanStop);
    }

    [Fact]
    public void Stopped_is_only_said_once_the_pages_are_released()
    {
        Assert.StartsWith("Stopping.", AgentActivity.Build([Session(closed: true)], Now).Single().Status);
        var done = AgentActivity.Build([Session(closed: true, cleaned: true)], Now).Single();
        Assert.StartsWith("Stopped.", done.Status);
        Assert.False(done.CanStop);
    }

    [Fact]
    public void An_expired_session_is_not_described_as_running()
    {
        Assert.StartsWith("Expired.", AgentActivity.Build([Session(minutesLeft: -1)], Now).Single().Status);
    }

    [Fact]
    public void Recent_actions_are_newest_first_and_refusals_say_why()
    {
        var audit = new[]
        {
            new AuditEntry(Now.AddSeconds(-30), "Navigate", "https://github.com/x", true, ""),
            new AuditEntry(Now.AddSeconds(-10), "Click", "#buy", false, "outside approved domains"),
        };
        var lines = AgentActivity.Build([Session(audit: audit)], Now).Single().Recent;
        Assert.Equal(2, lines.Count);
        Assert.Contains("Click #buy: refused, outside approved domains", lines[0]);   // an unknown code is shown as given, not guessed at
        Assert.Contains("Navigate https://github.com/x: done", lines[1]);
    }

    [Fact]
    public void A_long_target_is_cut_so_a_typed_secret_or_long_url_is_not_laid_out_whole()
    {
        var audit = new[] { new AuditEntry(Now, "TypeNonSecret", new string('x', 300), true, "") };
        var line = AgentActivity.Build([Session(audit: audit)], Now).Single().Recent.Single();
        Assert.True(line.Length < 140);
        Assert.Contains("…", line);
    }

    [Fact]
    public void Running_sessions_are_listed_before_stopped_ones()
    {
        var cards = AgentActivity.Build([Session("Old", cleaned: true), Session("Live")], Now);
        Assert.Equal(["Live", "Old"], cards.Select(c => c.Agent));
    }

    [Fact]
    public void Nothing_running_is_an_empty_list_not_an_error() => Assert.Empty(AgentActivity.Build([], Now));

    [Theory]
    [InlineData("domain_not_allowed:evil.test", "evil.test is not on the approved list")]
    [InlineData("action_not_granted:Click", "you did not allow it to click")]
    [InlineData("data_class_denied:Sensitive", "the page is sensitive, which you kept off limits")]
    [InlineData("hard:secret_field_selector", "it tried to type into a password or secret field")]
    [InlineData("session_expired", "the session ran out of time")]
    public void Refusals_are_said_in_words_a_person_can_act_on(string code, string words) => Assert.Equal(words, AgentActivity.RefusalWords(code));
}
