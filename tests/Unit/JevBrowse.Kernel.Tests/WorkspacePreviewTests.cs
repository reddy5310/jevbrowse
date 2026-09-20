using JevBrowse.Domain;
using JevBrowse.VirtualTabs;
using Xunit;

namespace JevBrowse.Kernel.Tests;

public class WorkspacePreviewTests
{
    private static Workspace Ws(string name, IdentityContainer c = IdentityContainer.Personal) => new(ContextId.New(), name) { Container = c };
    private static VirtualTab Tab(Workspace w, string host, string title = "") => new(ResourceId.New(), new Uri($"https://{host}/"), title, w.Id);
    private static IReadOnlyList<WorkspacePreview> Build(IEnumerable<Workspace> ws, IEnumerable<VirtualTab> tabs, ContextId active, Func<VirtualTab, string?>? thumb = null, ResourceId? activeTab = null) =>
        WorkspacePreviews.Build(ws, tabs, active, activeTab, t => t.Title == "" ? t.Url.Host : t.Title, thumb ?? (_ => "C:\\thumb.png"));

    [Fact]
    public void The_current_workspace_comes_first_and_each_card_says_in_words_what_is_inside()
    {
        var work = Ws("Work"); var home = Ws("Home");
        var tabs = new[] { Tab(home, "a.com"), Tab(work, "b.com"), Tab(work, "c.com") };
        var cards = Build([home, work], tabs, work.Id);
        Assert.Equal(["Work", "Home"], cards.Select(c => c.Name));
        Assert.True(cards[0].IsActive);
        Assert.Equal("2 tabs, 0 awake · Personal", cards[0].Summary);   // new tabs are sleeping until opened
        Assert.Equal("1 tab, 0 awake · Personal", cards[1].Summary);
    }

    [Fact]
    public void A_private_session_is_left_out_unless_it_is_the_one_you_are_in()
    {
        var normal = Ws("Work"); var secret = Ws("Private session", IdentityContainer.Private);
        var tabs = new[] { Tab(normal, "a.com"), Tab(secret, "bank.example") };
        Assert.DoesNotContain(Build([normal, secret], tabs, normal.Id), c => c.Name == "Private session");
        Assert.Contains(Build([normal, secret], tabs, secret.Id), c => c.Name == "Private session");
    }

    [Fact]
    public void A_private_session_never_carries_a_saved_image_even_when_it_is_the_one_you_are_in()
    {
        var secret = Ws("Private session", IdentityContainer.Private);
        var card = Build([secret], [Tab(secret, "bank.example")], secret.Id).Single();
        Assert.All(card.Tabs, t => Assert.Null(t.ThumbnailPath));
        Assert.Contains("nothing is kept after it ends", card.Summary);
    }

    [Fact]
    public void A_normal_workspace_shows_the_image_the_caller_found()
    {
        var w = Ws("Work");
        var card = Build([w], [Tab(w, "a.com")], w.Id).Single();
        Assert.Equal("C:\\thumb.png", card.Tabs.Single().ThumbnailPath);
    }

    [Fact]
    public void Long_lists_are_cut_and_the_cut_is_counted_not_hidden()
    {
        var w = Ws("Work");
        var tabs = Enumerable.Range(0, 9).Select(i => Tab(w, $"s{i}.com")).ToList();
        var card = Build([w], tabs, w.Id).Single();
        Assert.Equal(6, card.Tabs.Count);
        Assert.Equal(3, card.HiddenTabs);
        Assert.Equal(9, card.TabCount);
    }

    [Fact]
    public void The_tab_you_are_looking_at_is_marked_and_an_empty_workspace_says_so()
    {
        var w = Ws("Work"); var empty = Ws("Empty");
        var t = Tab(w, "a.com");
        var cards = Build([w, empty], [t], w.Id, activeTab: t.Id);
        Assert.True(cards.First(c => c.Name == "Work").Tabs.Single().IsActive);
        Assert.Equal("No tabs · Personal", cards.First(c => c.Name == "Empty").Summary);
    }
}
