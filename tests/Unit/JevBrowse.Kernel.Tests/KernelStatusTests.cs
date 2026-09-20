using JevBrowse.Domain;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

public class KernelStatusTests
{
    private static readonly ResourceId Id = ResourceId.New();

    [Theory]
    [InlineData("activated", "live=2")]
    [InlineData("navigated", "Some page")]
    [InlineData("loaded", "https://example.com/")]
    [InlineData("opened", "example.com")]
    [InlineData("closed", "")]
    [InlineData("signals", "Unknown")]
    [InlineData("protection", "DirtyForm")]
    [InlineData("decision", "virtualized")]
    [InlineData("workspace-switched", "abc")]
    [InlineData("pinned", "pinned")]
    [InlineData("moved", "abc")]
    public void Bookkeeping_events_say_nothing_rather_than_echoing_themselves(string kind, string reason) =>
        Assert.Null(KernelStatus.For(new(kind, Id, reason)));

    [Fact]
    public void A_tab_the_scheduler_put_to_sleep_is_reported_in_plain_words()
    {
        var text = KernelStatus.For(new("virtualized", Id, "Scheduler"));
        Assert.Equal("A tab went to sleep to save memory.", text);
    }

    [Fact]
    public void A_first_load_is_not_announced_as_waking_a_sleeping_tab()
    {
        Assert.Null(KernelStatus.For(new("restored", Id, "806 ms" + TabKernel.FirstLoadSuffix)));
        Assert.Equal("Woke a sleeping tab in 806 ms.", KernelStatus.For(new("restored", Id, "806 ms")));
    }

    [Fact]
    public void A_tab_the_user_put_to_sleep_is_not_announced_back_to_them() =>
        Assert.Null(KernelStatus.For(new("virtualized", Id, "User")));

    [Fact]
    public void No_message_leaks_an_internal_state_or_event_name()
    {
        foreach (var e in new KernelEvent[]
        {
            new("virtualized", Id, "Scheduler"), new("restored", Id, "520 ms"),
            new("checkpoint-failed", Id, "TimedOut: page did not respond"), new("virtualize-failed", Id, "boom"),
        })
        {
            var text = KernelStatus.For(e)!;
            // Whole-word: "Cause" must not match inside "because".
            foreach (var jargon in new[] { "Virtual", "Hibernat", "checkpoint", "Scheduler", "TimedOut", "renderer", "Cause" })
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, @"\b" + jargon, System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                    $"'{jargon}' leaked into: {text}");
        }
    }
}
