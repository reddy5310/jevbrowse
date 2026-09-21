using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.TrustOS;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

/// <summary>Focused regressions for the private-alpha additions: session resume, and remembered permission decisions.</summary>
public class SessionResumeTests
{
    private static Workspace Ws(string name, IdentityContainer c = IdentityContainer.Personal) => new(ContextId.New(), name) { Container = c };
    private static VirtualTab Tab(Workspace w) => new(ResourceId.New(), new Uri("https://example.org/" + Guid.NewGuid().ToString("N")[..4]), "", w.Id);

    [Fact]
    public void The_remembered_tab_is_resumed_in_its_own_workspace()
    {
        var a = Ws("A"); var b = Ws("B"); var ta = Tab(a); var tb1 = Tab(b); var tb2 = Tab(b);
        var c = SessionResume.Choose(b.Id.ToString(), tb1.Id.ToString(), [a, b], [ta, tb1, tb2]);
        Assert.Equal((b.Id, tb1.Id), (c!.Workspace, c.Tab));
    }

    [Fact]
    public void A_tab_that_no_longer_exists_falls_back_to_the_last_tab_of_the_remembered_workspace()
    {
        var a = Ws("A"); var t1 = Tab(a); var t2 = Tab(a);
        var c = SessionResume.Choose(a.Id.ToString(), ResourceId.New().ToString(), [a], [t1, t2]);
        Assert.Equal((a.Id, (ResourceId?)t2.Id), (c!.Workspace, c.Tab));
    }

    [Fact]
    public void A_workspace_with_no_tabs_is_resumed_empty_so_the_app_can_open_a_start_page_there()
    {
        var a = Ws("A");
        var c = SessionResume.Choose(a.Id.ToString(), null, [a], []);
        Assert.Equal(a.Id, c!.Workspace);
        Assert.Null(c.Tab);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("not-a-guid", "also-not")]
    public void Missing_or_garbled_ids_fall_back_to_the_normal_start(string? w, string? t)
    {
        var a = Ws("A");
        Assert.Null(SessionResume.Choose(w, t, [a], [Tab(a)]));
    }

    [Fact]
    public void A_deleted_workspace_falls_back()
    {
        var a = Ws("A");
        Assert.Null(SessionResume.Choose(ContextId.New().ToString(), ResourceId.New().ToString(), [a], [Tab(a)]));
    }

    [Theory]
    [InlineData(IdentityContainer.Private)]
    [InlineData(IdentityContainer.Disposable)]
    public void Private_and_disposable_workspaces_are_never_resumed_even_if_their_ids_were_saved(IdentityContainer container)
    {
        var ordinary = Ws("Ordinary"); var hidden = Ws("Hidden", container);
        var tHidden = Tab(hidden);
        // by tab id, by workspace id, and by both: none may select it
        Assert.Null(SessionResume.Choose(null, tHidden.Id.ToString(), [ordinary, hidden], [tHidden]));
        Assert.Null(SessionResume.Choose(hidden.Id.ToString(), null, [ordinary, hidden], [tHidden]));
        Assert.Null(SessionResume.Choose(hidden.Id.ToString(), tHidden.Id.ToString(), [ordinary, hidden], [tHidden]));
    }

    [Fact]
    public void A_tab_whose_workspace_was_replaced_by_a_private_one_is_not_resumed_but_the_ordinary_workspace_still_is()
    {
        var ordinary = Ws("Ordinary"); var t1 = Tab(ordinary);
        var priv = Ws("Priv", IdentityContainer.Private); var tp = Tab(priv);
        var c = SessionResume.Choose(ordinary.Id.ToString(), tp.Id.ToString(), [ordinary, priv], [t1, tp]);
        Assert.Equal((ordinary.Id, (ResourceId?)t1.Id), (c!.Workspace, c.Tab));
    }
}

public class RememberedPermissionTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    public void Dispose() => _db.Dispose();
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(10);

    private static string Key(IdentityContainer c, string url) => PermissionKey.For(c, ContextId.Default, new Uri(url));

    [Fact]
    public void Decisions_are_listed_for_exactly_one_origin_and_identity_and_expired_ones_are_not_shown()
    {
        var repo = new SitePermissionsRepository(_db);
        var mine = Key(IdentityContainer.Personal, "https://meet.example.com/");
        repo.Set(mine, (int)PermissionKind.Camera, true, null, Now);
        repo.Set(mine, (int)PermissionKind.Microphone, false, Now.AddHours(1), Now);
        repo.Set(mine, (int)PermissionKind.Geolocation, true, Now.AddMinutes(-1), Now.AddHours(-2));                    // expired
        repo.Set(Key(IdentityContainer.Work, "https://meet.example.com/"), (int)PermissionKind.Camera, true, null, Now);   // same site, other identity
        repo.Set(Key(IdentityContainer.Personal, "https://other.example.com/"), (int)PermissionKind.Camera, true, null, Now);

        var rows = repo.ListForKey(mine, Now);

        Assert.Equal([(int)PermissionKind.Camera, (int)PermissionKind.Microphone], rows.Select(r => r.Kind).ToArray());
    }

    [Fact]
    public void Reset_removes_only_what_was_asked_and_the_policy_then_asks_again()
    {
        var repo = new SitePermissionsRepository(_db);
        var key = Key(IdentityContainer.Personal, "https://meet.example.com/");
        var other = Key(IdentityContainer.Work, "https://meet.example.com/");
        repo.Set(key, (int)PermissionKind.Camera, true, null, Now);
        repo.Set(key, (int)PermissionKind.Microphone, false, null, Now);
        repo.Set(other, (int)PermissionKind.Camera, true, null, Now);
        PermissionPolicy Policy() => new((k, kind) => repo.Get(k, (int)kind) is { } r ? new PermissionGrant(k, kind, r.Allowed, r.ExpiresAt) : null, () => Now);
        Assert.Equal(PermissionVerdict.Allow, Policy().Decide(key, PermissionKind.Camera));

        Assert.Equal(1, repo.Delete(key, (int)PermissionKind.Camera));

        Assert.Equal(PermissionVerdict.Ask, Policy().Decide(key, PermissionKind.Camera));          // decided normally again
        Assert.Equal(PermissionVerdict.Deny, Policy().Decide(key, PermissionKind.Microphone));     // the other kind is still blocked
        Assert.Equal(PermissionVerdict.Allow, Policy().Decide(other, PermissionKind.Camera));      // another identity is untouched
        Assert.Equal(1, repo.Delete(key));                                                          // all kinds for that key
        Assert.Empty(repo.ListForKey(key, Now));
        Assert.Single(repo.ListForKey(other, Now));
    }
}
