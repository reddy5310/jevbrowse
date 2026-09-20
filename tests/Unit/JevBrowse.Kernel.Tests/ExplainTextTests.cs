using System.Text.RegularExpressions;
using JevBrowse.Domain;
using JevBrowse.ResourceOS;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

public class ExplainTextTests
{
    private static ScheduledAction Decision(params (string k, string v)[] reasons) =>
        new(ResourceId.New(), "virtualize", reasons.ToDictionary(x => x.k, x => x.v));

    private static ExplainFacts Facts(ResourceState state = ResourceState.Virtual, bool fg = false, ScheduledAction? d = null,
        PressureBand? band = PressureBand.Yellow, int most = 6, int awake = 3, params string[] keep) =>
        new(state, fg, d, band, most, awake, keep);

    private static string All(ExplainFacts f) { var v = ExplainText.Build(f); return v.Headline + "\n" + string.Join("\n", v.Lines); }

    [Fact]
    public void The_foreground_tab_says_it_stays_awake_because_you_are_looking_at_it() =>
        Assert.Equal("This is the tab you are looking at, so it stays awake.", ExplainText.Build(Facts(ResourceState.Hot, fg: true)).Headline);

    [Fact]
    public void A_tab_put_to_sleep_for_being_idle_says_how_long()
    {
        var v = ExplainText.Build(Facts(d: Decision(("trigger", "idle"), ("inactive_minutes", "12"))));
        Assert.Contains("It was put to sleep because it had not been used for 12 minutes.", v.Lines);
    }

    [Fact]
    public void A_tab_put_to_sleep_for_the_limit_names_the_limit()
    {
        var v = ExplainText.Build(Facts(d: Decision(("trigger", "over_budget"), ("inactive_minutes", "1")), most: 5));
        Assert.Contains("you had more tabs open than the 5 JevBrowse keeps awake at once", string.Join(" ", v.Lines));
    }

    [Fact]
    public void A_tab_that_was_spared_says_why_using_the_same_words_as_the_tab_row()
    {
        var v = ExplainText.Build(Facts(ResourceState.Warm, d: Decision(("trigger", "idle"), ("inactive_minutes", "30"), ("skipped", "protected")), keep: ["using microphone", "unsaved typing"]));
        Assert.Contains("did not, because it is using microphone, unsaved typing", string.Join(" ", v.Lines));
    }

    [Theory]
    [InlineData("min_residency", "woken up only a moment ago")]
    [InlineData("cooldown", "woken again very recently")]
    [InlineData("hibernation_window_limit", "pacing itself")]
    public void Every_reason_a_tab_can_be_spared_has_a_plain_sentence(string reason, string expected) =>
        Assert.Contains(expected, string.Join(" ", ExplainText.Build(Facts(ResourceState.Warm, d: Decision(("trigger", "idle"), ("inactive_minutes", "9"), ("skipped", reason)))).Lines));

    [Theory]
    [InlineData(PressureBand.Green, "Memory is comfortable")]
    [InlineData(PressureBand.Yellow, "Memory use is moderate")]
    [InlineData(PressureBand.Orange, "Memory is getting tight")]
    [InlineData(PressureBand.Red, "Memory is very tight")]
    public void Pressure_is_described_not_named(PressureBand band, string expected) =>
        Assert.Contains(expected, string.Join(" ", ExplainText.Build(Facts(band: band)).Lines));

    [Fact]
    public void Nothing_the_scheduler_says_reaches_the_person_in_its_own_vocabulary()
    {
        var all = string.Join("\n", new[]
        {
            All(Facts(ResourceState.Hot, fg: true)),
            All(Facts(d: Decision(("trigger", "over_budget"), ("inactive_minutes", "4"), ("system_pressure", "YELLOW")))),
            All(Facts(ResourceState.Warm, d: Decision(("trigger", "idle"), ("inactive_minutes", "4"), ("skipped", "min_residency")))),
            All(Facts(ResourceState.Warm, d: Decision(("trigger", "idle"), ("inactive_minutes", "4"), ("skipped", "hibernation_window_limit")))),
        });
        foreach (var jargon in new[] { "scheduler", "budget", "band", "renderer", "virtualiz", "residency", "cooldown", "veto", "over_budget", "inactive_minutes", "GREEN", "YELLOW" })
            Assert.False(Regex.IsMatch(all, Regex.Escape(jargon), RegexOptions.IgnoreCase), $"'{jargon}' leaked into:\n{all}");
    }
}
