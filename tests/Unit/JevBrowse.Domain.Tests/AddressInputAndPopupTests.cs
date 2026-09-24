using JevBrowse.Domain;

namespace JevBrowse.Domain.Tests;

public class AddressInputTests
{
    [Theory]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("  example.com/path  ", "https://example.com/path")]
    [InlineData("localhost:3000/app", "http://localhost:3000/app")]
    [InlineData("jev://welcome/", "jev://welcome/")]
    public void Addresses_navigate(string typed, string expected)
    {
        var r = AddressInput.Resolve(typed);
        Assert.Equal(AddressKind.Navigate, r.Kind);
        Assert.Equal(expected, r.Url!.ToString());
    }

    [Theory]
    [InlineData("cats")]
    [InlineData("how do i fix this")]
    [InlineData("what is 2.5 times 3")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<b>x</b>")]
    public void Anything_else_is_a_search_and_never_a_navigation_to_a_dangerous_scheme(string typed)
    {
        var r = AddressInput.Resolve(typed);
        Assert.Equal(AddressKind.Search, r.Kind);
        Assert.StartsWith(AddressInput.SearchUrl, r.Url!.ToString());
        Assert.Equal("https", r.Url.Scheme);
    }

    [Theory]
    [InlineData("https://")]
    [InlineData("http://")]
    [InlineData("https:// example.com")]
    [InlineData("://")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/x")]
    [InlineData("chrome://settings")]
    [InlineData("view-source://example.com")]
    [InlineData("jev://something-else")]
    public void Incomplete_or_unsupported_addresses_are_refused_with_a_reason_and_never_throw(string typed)
    {
        var r = AddressInput.Resolve(typed);
        Assert.Equal(AddressKind.Invalid, r.Kind);
        Assert.Null(r.Url);
        Assert.False(string.IsNullOrWhiteSpace(r.Message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_typed_does_nothing(string? typed)
    {
        var r = AddressInput.Resolve(typed);
        Assert.Equal(AddressKind.Invalid, r.Kind);
        Assert.Equal("", r.Message);
    }

    [Fact]
    public void A_huge_paste_is_refused_not_navigated()
        => Assert.Equal(AddressKind.Invalid, AddressInput.Resolve("https://example.com/" + new string('a', 9000)).Kind);

    [Fact]
    public void Random_input_never_throws()
    {
        var rng = new Random(7);
        for (var i = 0; i < 5000; i++)
        {
            var s = new string(Enumerable.Range(0, rng.Next(0, 40)).Select(_ => (char)rng.Next(32, 0x2FF)).ToArray());
            var r = AddressInput.Resolve(s);
            Assert.True(r.Kind != AddressKind.Navigate || r.Url!.Scheme is "http" or "https" or "jev");
        }
    }
}

public class CtrlEnterTests
{
    [Theory]
    [InlineData("example", "https://www.example.com/")]
    [InlineData("Example", "https://www.example.com/")]
    [InlineData("example.com", "https://www.example.com/")]
    [InlineData("www.example.com", "https://www.example.com/")]
    [InlineData("example.co.uk", "https://www.example.co.uk/")]
    [InlineData("  example  ", "https://www.example.com/")]
    public void A_bare_word_becomes_www_dot_word_dot_com(string typed, string expected)
    {
        var r = AddressInput.ResolveForceNavigate(typed);
        Assert.Equal(AddressKind.Navigate, r.Kind);
        Assert.Equal(expected, r.Url!.ToString());
    }

    [Fact]
    public void An_address_that_already_has_a_scheme_is_left_alone()
    {
        var r = AddressInput.ResolveForceNavigate("http://example.com/path");
        Assert.Equal(AddressKind.Navigate, r.Kind);
        Assert.Equal("http://example.com/path", r.Url!.ToString());
    }

    [Fact]
    public void Localhost_is_not_turned_into_a_dot_com()
    {
        var r = AddressInput.ResolveForceNavigate("localhost:3000");
        Assert.Equal(AddressKind.Navigate, r.Kind);
        Assert.Equal("http://localhost:3000/", r.Url!.ToString());
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("")]
    [InlineData("   ")]
    public void Anything_with_a_space_or_empty_is_still_a_search_or_invalid_not_forced_into_a_url(string typed)
    {
        var r = AddressInput.ResolveForceNavigate(typed);
        Assert.NotEqual(AddressKind.Navigate, r.Kind);
    }

    [Fact]
    public void The_chosen_search_engine_still_applies_when_Ctrl_Enter_falls_back_to_a_search()
    {
        var r = AddressInput.ResolveForceNavigate("two words", SearchEngines.Find("bing"));
        Assert.Equal(AddressKind.Search, r.Kind);
        Assert.StartsWith("https://www.bing.com/search", r.Url!.ToString());
    }
}

public class PopupPolicyTests
{
    [Fact] public void A_clicked_link_in_the_page_in_front_opens_a_tab() => Assert.True(PopupPolicy.Decide(true, false, true, "https").Allow);
    [Fact] public void A_popup_nobody_asked_for_is_refused() => Assert.False(PopupPolicy.Decide(false, false, true, "https").Allow);
    [Fact] public void An_agent_page_can_never_open_a_window() => Assert.False(PopupPolicy.Decide(true, true, true, "https").Allow);
    [Fact] public void A_page_in_the_background_can_not_pull_a_tab_to_the_front() => Assert.False(PopupPolicy.Decide(true, false, false, "https").Allow);

    [Theory]
    [InlineData("javascript")]
    [InlineData("file")]
    [InlineData("about")]
    [InlineData(null)]
    public void Only_web_addresses_are_opened(string? scheme) => Assert.False(PopupPolicy.Decide(true, false, true, scheme).Allow);
}
