using JevBrowse.Domain;
using JevBrowse.TrustOS;

namespace JevBrowse.TrustOS.Tests;

public class DataClassifierTests
{
    private static readonly DataClassifier C = new();

    [Theory]
    [InlineData("https://en.wikipedia.org/wiki/Cat", DataClass.Public)]
    [InlineData("https://learn.microsoft.com/webview2", DataClass.Public)]
    [InlineData("https://mail.google.com/mail/u/0/#inbox", DataClass.Authenticated)]
    [InlineData("https://github.com/settings/profile", DataClass.Authenticated)]
    [InlineData("https://accounts.example.com/login", DataClass.Authenticated)]
    [InlineData("http://localhost:3000/", DataClass.Authenticated)]
    [InlineData("https://netbanking.hdfcbank.com/", DataClass.Sensitive)]
    [InlineData("https://www.paypal.com/myaccount", DataClass.Sensitive)]
    [InlineData("https://shop.example.com/checkout", DataClass.Sensitive)]
    [InlineData("https://kite.zerodha.com/", DataClass.Sensitive)]
    public void Url_heuristics(string url, DataClass expected) =>
        Assert.Equal(expected, C.Classify(new Uri(url), IdentityContainer.Personal, PageSignals.None));

    [Fact]
    public void Password_field_makes_any_page_secret()
    {
        Assert.Equal(DataClass.Secret, C.Classify(new Uri("https://en.wikipedia.org/w/index.php?title=Special:UserLogin"), IdentityContainer.Personal, PageSignals.PasswordField));
        Assert.Equal(DataClass.Secret, C.Classify(new Uri("https://shop.example.com/pay"), IdentityContainer.Work, PageSignals.PaymentField));
    }

    [Fact]
    public void Independent_evidence_combines_by_the_stricter_result()
    {
        // an "authenticated" signal must not downgrade a banking URL
        Assert.Equal(DataClass.Sensitive, C.Classify(new Uri("https://netbanking.hdfcbank.com/"), IdentityContainer.Personal, PageSignals.Authenticated));
        // and an unrecognised URL becomes AUTHENTICATED as soon as the page shows it is logged in (private repo, dashboard)
        Assert.Equal(DataClass.Public, C.Classify(new Uri("https://github.com/company/project"), IdentityContainer.Personal, PageSignals.None));
        Assert.Equal(DataClass.Authenticated, C.Classify(new Uri("https://github.com/company/project"), IdentityContainer.Personal, PageSignals.Authenticated));
        // a password field beats a URL that looked public and a login signal
        Assert.Equal(DataClass.Secret, C.Classify(new Uri("https://news.example.com/"), IdentityContainer.Personal, PageSignals.Authenticated | PageSignals.PasswordField));
    }

    [Fact]
    public void User_override_cannot_hide_a_password_field_but_does_outrank_url_heuristics()
    {
        var c = new DataClassifier(site => site == "corp.test" ? DataClass.Public : null);
        Assert.Equal(DataClass.Public, c.Classify(new Uri("https://accounts.corp.test/login"), IdentityContainer.Personal, PageSignals.None));      // user's own site, their call
        Assert.Equal(DataClass.Secret, c.Classify(new Uri("https://accounts.corp.test/login"), IdentityContainer.Personal, PageSignals.PasswordField));
        Assert.Equal(DataClass.Secret, c.Classify(new Uri("https://accounts.corp.test/pay"), IdentityContainer.Personal, PageSignals.PaymentField));
    }

    [Fact]
    public void Ephemeral_container_wins_over_everything()
    {
        Assert.Equal(DataClass.Ephemeral, C.Classify(new Uri("https://en.wikipedia.org/"), IdentityContainer.Private, PageSignals.None));
        Assert.Equal(DataClass.Ephemeral, C.Classify(new Uri("https://bank.example/"), IdentityContainer.Disposable, PageSignals.PasswordField));
    }

    [Fact]
    public void User_override_applies_but_password_still_raises_to_secret()
    {
        var c = new DataClassifier(site => site == "example.com" ? DataClass.Sensitive : null);
        Assert.Equal(DataClass.Sensitive, c.Classify(new Uri("https://www.example.com/"), IdentityContainer.Personal, PageSignals.None));
        Assert.Equal(DataClass.Secret, c.Classify(new Uri("https://www.example.com/"), IdentityContainer.Personal, PageSignals.PasswordField));
        // override can also lower a heuristic: user says their intranet "dashboard." host is public
        var lower = new DataClassifier(site => site == "intranet.test" ? DataClass.Public : null);
        Assert.Equal(DataClass.Public, lower.Classify(new Uri("https://dashboard.intranet.test/"), IdentityContainer.Personal, PageSignals.None));
    }
}

public class TrustPolicyTests
{
    private static readonly DefaultTrustPolicy P = new();
    private static ResourceContext Ctx(DataClass c, IdentityContainer k = IdentityContainer.Personal) => new(new Uri("https://x.test/"), c, k);

    [Fact]
    public void Matrix_gets_stricter_as_class_rises()
    {
        var ops = Enum.GetValues<DataOperation>();
        var classes = new[] { DataClass.Public, DataClass.Authenticated, DataClass.Sensitive, DataClass.Secret, DataClass.Ephemeral };
        foreach (var op in ops)
            for (int i = 1; i < classes.Length; i++)
            {
                var looser = P.Evaluate(Ctx(classes[i - 1]), op).Allowed;
                var stricter = P.Evaluate(Ctx(classes[i]), op).Allowed;
                Assert.False(!looser && stricter, $"{op}: {classes[i]} allowed but {classes[i - 1]} denied");
            }
    }

    [Fact]
    public void Table_A10_defaults()
    {
        Assert.True(P.Evaluate(Ctx(DataClass.Public), DataOperation.IndexContent).Allowed);
        Assert.True(P.Evaluate(Ctx(DataClass.Authenticated), DataOperation.PersistThumbnail).Allowed);
        Assert.False(P.Evaluate(Ctx(DataClass.Authenticated), DataOperation.IndexContent).Allowed);
        Assert.False(P.Evaluate(Ctx(DataClass.Sensitive), DataOperation.PersistThumbnail).Allowed);
        Assert.True(P.Evaluate(Ctx(DataClass.Sensitive), DataOperation.PersistTabRow).Allowed);
        Assert.False(P.Evaluate(Ctx(DataClass.Secret), DataOperation.PersistCheckpoint).Allowed);
        Assert.True(P.Evaluate(Ctx(DataClass.Secret), DataOperation.PersistTabRow).Allowed); // URL/title always (Table A.5)
    }

    [Fact]
    public void Ephemeral_container_denies_everything_including_tab_rows()
    {
        foreach (var op in Enum.GetValues<DataOperation>())
            Assert.False(P.Evaluate(Ctx(DataClass.Public, IdentityContainer.Private), op).Allowed, op.ToString());
    }

    [Fact]
    public void Cloud_ai_and_agents_are_never_allowed_by_default_above_public()
    {
        foreach (var c in new[] { DataClass.Authenticated, DataClass.Sensitive, DataClass.Secret })
        {
            Assert.False(P.Evaluate(Ctx(c), DataOperation.SendToCloudAI).Allowed);
            Assert.False(P.Evaluate(Ctx(c), DataOperation.ExposeToAgent).Allowed);
        }
    }
}

public class PermissionKeyTests
{
    private static readonly ContextId W1 = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ContextId W2 = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void A_grant_applies_to_one_exact_origin_in_one_identity()
    {
        var k = PermissionKey.For(IdentityContainer.Work, W1, new Uri("https://a.example.com/page?x=1"));
        Assert.Equal(k, PermissionKey.For(IdentityContainer.Work, W2, new Uri("https://A.EXAMPLE.com/other")));            // path/case/workspace don't matter for a persistent container
        Assert.NotEqual(k, PermissionKey.For(IdentityContainer.Personal, W1, new Uri("https://a.example.com/")));          // another identity
        Assert.NotEqual(k, PermissionKey.For(IdentityContainer.Work, W1, new Uri("http://a.example.com/")));               // scheme
        Assert.NotEqual(k, PermissionKey.For(IdentityContainer.Work, W1, new Uri("https://a.example.com:8443/")));         // port
        Assert.NotEqual(k, PermissionKey.For(IdentityContainer.Work, W1, new Uri("https://b.example.com/")));             // sibling subdomain
        Assert.NotEqual(k, PermissionKey.For(IdentityContainer.Work, W1, new Uri("https://example.com/")));               // parent domain
    }

    [Fact]
    public void Ephemeral_identities_are_per_workspace_and_never_persisted()
    {
        var a = PermissionKey.For(IdentityContainer.Private, W1, new Uri("https://x.test/"));
        var b = PermissionKey.For(IdentityContainer.Private, W2, new Uri("https://x.test/"));
        Assert.NotEqual(a, b);                                                     // two private sessions never share a grant
        Assert.False(PermissionKey.MayPersist(IdentityContainer.Private));
        Assert.False(PermissionKey.MayPersist(IdentityContainer.Disposable));
        Assert.True(PermissionKey.MayPersist(IdentityContainer.Personal));
    }
}

public class PermissionPolicyTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch.AddDays(1);

    [Fact]
    public void Defaults_follow_table_A7()
    {
        var p = new PermissionPolicy((_, _) => null, () => T0);
        Assert.Equal(PermissionVerdict.Deny, p.Decide("x.test", PermissionKind.Notifications));
        Assert.Equal(PermissionVerdict.Ask, p.Decide("x.test", PermissionKind.Camera));
        Assert.Equal(PermissionVerdict.Ask, p.Decide("x.test", PermissionKind.Geolocation));
        Assert.Equal(PermissionVerdict.Deny, p.Decide("x.test", PermissionKind.Midi));
    }

    [Fact]
    public void Temporary_grant_expires()
    {
        var now = T0;
        var grant = new PermissionGrant("x.test", PermissionKind.Camera, true, T0.AddHours(1));
        var p = new PermissionPolicy((s, k) => s == "x.test" && k == PermissionKind.Camera ? grant : null, () => now);
        Assert.Equal(PermissionVerdict.Allow, p.Decide("x.test", PermissionKind.Camera));
        now = T0.AddMinutes(59); Assert.Equal(PermissionVerdict.Allow, p.Decide("x.test", PermissionKind.Camera));
        now = T0.AddMinutes(61); Assert.Equal(PermissionVerdict.Ask, p.Decide("x.test", PermissionKind.Camera));
    }

    [Fact]
    public void Explicit_block_overrides_default_ask()
    {
        var p = new PermissionPolicy((_, _) => new PermissionGrant("x.test", PermissionKind.Geolocation, false, null), () => T0);
        Assert.Equal(PermissionVerdict.Deny, p.Decide("x.test", PermissionKind.Geolocation));
    }
}
