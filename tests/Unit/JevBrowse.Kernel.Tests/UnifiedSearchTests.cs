using JevBrowse.VirtualTabs;
using Xunit;

namespace JevBrowse.Kernel.Tests;

public class UnifiedSearchTests
{
    private static SearchEntry Tab(string title, string detail = "", string key = "") => new(SearchKind.Tab, title, detail, key == "" ? "tab:" + title : key);
    private static SearchEntry Cmd(string title) => new(SearchKind.Command, title, "", "cmd:" + title);
    private static SearchEntry Ws(string title) => new(SearchKind.Workspace, title, "", "ws:" + title);

    [Fact]
    public void Every_word_has_to_match_somewhere()
    {
        var r = UnifiedSearch.Rank("shield site", [Cmd("Shield: toggle for this site"), Cmd("Shield lists"), Cmd("Open a site")]);
        Assert.Equal(["Shield: toggle for this site"], r.Select(x => x.Entry.Title));
    }

    [Fact]
    public void A_title_that_starts_with_the_word_beats_one_that_only_contains_it_which_beats_the_detail()
    {
        var r = UnifiedSearch.Rank("wiki", [Tab("Some notes", "wikipedia.org"), Tab("Awikia thing"), Tab("Wikipedia home")]);
        Assert.Equal(["Wikipedia home", "Awikia thing", "Some notes"], r.Select(x => x.Entry.Title));
    }

    [Fact]
    public void Matching_is_case_blind_and_a_word_can_start_anywhere_in_the_title()
    {
        var r = UnifiedSearch.Rank("HIBER", [Cmd("Pin: never hibernate this tab")]);
        Assert.Single(r);
    }

    [Fact]
    public void Many_commands_cannot_push_the_one_matching_tab_off_the_list()
    {
        var cands = Enumerable.Range(0, 30).Select(i => Cmd($"Memory mode {i}")).Append(Tab("Memory usage dashboard")).ToList();
        var r = UnifiedSearch.Rank("memory", cands);
        Assert.Contains(r, x => x.Entry.Kind == SearchKind.Tab);
        Assert.Equal(8, r.Count(x => x.Entry.Kind == SearchKind.Command));
    }

    [Fact]
    public void A_page_hit_from_browser_memory_is_kept_even_when_the_words_do_not_appear_in_its_title()
    {
        // Its own index matched a word form ("processes" for "process") that this ranking does not understand.
        var page = new SearchEntry(SearchKind.Page, "WebView2 architecture", "…", "page:1", PreMatched: 55);
        var r = UnifiedSearch.Rank("process model", [page, Cmd("Shield lists")]);
        Assert.Equal(["WebView2 architecture"], r.Select(x => x.Entry.Title));
    }

    [Fact]
    public void Nothing_typed_lists_groups_in_order_and_keeps_the_callers_order_within_a_group()
    {
        var r = UnifiedSearch.Rank("", [Cmd("b"), Tab("t2"), Ws("w"), Tab("t1"), Cmd("a")]);
        Assert.Equal(["t2", "t1", "w", "b", "a"], r.Select(x => x.Entry.Title));
    }

    [Fact]
    public void Nothing_matches_gives_an_empty_list_not_an_error()
    {
        Assert.Empty(UnifiedSearch.Rank("zzzz", [Tab("a"), Cmd("b")]));
        Assert.Empty(UnifiedSearch.Rank(null, []));
    }
}
