using System.Text.RegularExpressions;
using JevBrowse.Domain;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

public class ReceiptRowsTests
{
    private static ReceiptFacts Facts(int checkedByShield = 0, int third = 0, int hosts = 0, int blocked = 0, int awake = 1, string[]? reasons = null) =>
        new("example.com", TimeSpan.FromSeconds(15), checkedByShield, third, hosts, blocked, 156, awake,
            "Not assessed", "This page isn't added to saved-page search or sent to JevBrowse's cloud AI.", "Personal", reasons ?? []);

    private static string All(ReceiptFacts f) => string.Join("\n", ReceiptRows.Build(f).Select(r => r.Label + ": " + r.Value)) + "\n" + ReceiptRows.Title(f) + "\n" + ReceiptRows.Footnote;

    [Fact]
    public void The_receipt_says_what_happened_in_words_not_implementation_terms()
    {
        var text = All(Facts(checkedByShield: 40, third: 12, hosts: 5, blocked: 7, awake: 2));
        // "estimate" is ordinary English and is used on purpose; the old shouted "(ESTIMATE: equal share ... across live
        // renderers)" is what these terms guard against.
        foreach (var jargon in new[] { "renderer", "attributed", "measured since", "data class", "container", "V1", "procs" })
            Assert.False(Regex.IsMatch(text, @"\b" + jargon, RegexOptions.IgnoreCase), $"'{jargon}' leaked into:\n{text}");
    }

    [Fact]
    public void Shield_is_credited_only_with_what_it_checked_not_with_every_request_the_page_made()
    {
        // "Requests 0" beside a page that plainly loaded reads as a lie. The label names what the number is.
        var rows = ReceiptRows.Build(Facts(checkedByShield: 0)).ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("0 requests", rows["Checked by Shield"]);
        Assert.DoesNotContain("Requests", rows.Keys);
    }

    [Theory]
    [InlineData(1, "1 request")]
    [InlineData(2, "2 requests")]
    public void Counts_are_singular_when_they_are_one(int n, string expected) =>
        Assert.Equal(expected, ReceiptRows.Build(Facts(checkedByShield: n)).First(r => r.Label == "Checked by Shield").Value);

    [Fact]
    public void Nothing_sent_or_blocked_reads_as_nothing()
    {
        var rows = ReceiptRows.Build(Facts()).ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("nothing", rows["Sent to other sites"]);
        Assert.Equal("nothing", rows["Blocked"]);
        Assert.Equal("nothing: it can sleep when memory is needed", rows["Kept awake because"]);
    }

    [Fact]
    public void The_memory_figure_is_labelled_as_an_estimate_and_says_how_it_was_made()
    {
        var value = ReceiptRows.Build(Facts(awake: 2)).First(r => r.Label == "Memory").Value;
        Assert.StartsWith("about 78 MB", value);          // 156 shared between 2 awake tabs
        Assert.Contains("estimate", value);
        Assert.Contains("2 awake tabs", value);
    }

    [Fact]
    public void Reasons_for_staying_awake_come_from_the_same_words_as_the_tab_row_and_never_include_pinned()
    {
        var reasons = ProtectionPhrases.StayAwake(ProtectionFlags.KeepActive | ProtectionFlags.MicrophoneActive | ProtectionFlags.DirtyForm);
        Assert.Equal(["kept active", "using microphone", "unsaved typing"], reasons);
        Assert.DoesNotContain("pinned", ProtectionPhrases.StayAwake((ProtectionFlags)~0));   // placement is not a reason to stay awake
        var value = ReceiptRows.Build(Facts(reasons: [.. reasons])).First(r => r.Label == "Kept awake because").Value;
        Assert.Equal("kept active, using microphone, unsaved typing", value);
    }
}
