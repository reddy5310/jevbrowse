using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.TrustOS;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

/// <summary>A decision about one site must apply to that site only, and a stricter decision must take effect at once.</summary>
public class SiteDecisionTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("one.co.uk", "two.co.uk")]
    [InlineData("alice.github.io", "bob.github.io")]
    [InlineData("www.example.com", "shop.example.com")]
    public void A_decision_for_one_host_does_not_apply_to_an_unrelated_host_that_shares_its_last_labels(string decided, string other)
    {
        var repo = new SiteSettingsRepository(_db);
        repo.SetDataClassOverrideForHost(decided, (int)DataClass.Public);

        Assert.Equal((int)DataClass.Public, repo.DataClassOverrideForHost(decided));
        Assert.Null(repo.DataClassOverrideForHost(other));
        // Through the classifier, end to end: the other site stays Unknown, not Public.
        var classifier = new DataClassifier(h => repo.DataClassOverrideForHost(h) is { } c ? (DataClass)c : null);
        Assert.Equal(DataClass.Public, classifier.Classify(new Uri($"https://{decided}/"), IdentityContainer.Personal, default));
        Assert.Equal(DataClass.Unknown, classifier.Classify(new Uri($"https://{other}/page"), IdentityContainer.Personal, default));
    }

    [Fact]
    public void HostKey_is_the_exact_host_lowercased_without_a_trailing_dot()
    {
        Assert.Equal("one.co.uk", DataClassifier.HostKey("One.CO.uk."));
        Assert.Equal("alice.github.io", DataClassifier.HostKey("alice.github.io"));
    }

    [Fact]
    public void An_older_two_label_decision_that_is_stricter_still_protects_but_an_older_Public_no_longer_loosens_anything()
    {
        // Rows as older builds wrote them (exact_host = 0): the last two labels of whatever site the person was on.
        _db.Exec("INSERT INTO site_settings (site, shield_enabled, data_class, updated_at) VALUES ('bank.example', 1, 3, 0), ('co.uk', 1, 0, 0), ('example.com', 1, 2, 0)");
        var repo = new SiteSettingsRepository(_db);

        Assert.Equal((int)DataClass.Sensitive, repo.DataClassOverrideForHost("login.bank.example"));   // stricter: kept (over-protecting is the safe direction)
        Assert.Equal((int)DataClass.Authenticated, repo.DataClassOverrideForHost("www.example.com"));
        Assert.Null(repo.DataClassOverrideForHost("one.co.uk"));                                        // an old "Public" for co.uk applies to nobody
        Assert.Null(repo.ExactDataClassOverride("www.example.com"));                                    // the dialog shows "no decision for this host yet"
    }

    [Fact]
    public void A_new_exact_decision_replaces_an_older_row_and_wins_over_it()
    {
        _db.Exec("INSERT INTO site_settings (site, shield_enabled, data_class, updated_at) VALUES ('example.com', 1, 3, 0)");
        var repo = new SiteSettingsRepository(_db);

        repo.SetDataClassOverrideForHost("example.com", (int)DataClass.Public);

        Assert.Equal((int)DataClass.Public, repo.DataClassOverrideForHost("example.com"));
        Assert.Equal((int)DataClass.Public, repo.ExactDataClassOverride("example.com"));
    }

    [Fact]
    public void Shield_switches_are_untouched_by_a_data_class_decision_on_the_same_host()
    {
        var repo = new SiteSettingsRepository(_db);
        repo.SetShieldEnabled("example.com", false);
        repo.SetDataClassOverrideForHost("example.com", (int)DataClass.Sensitive);
        Assert.False(repo.IsShieldEnabled("example.com"));
    }

    // ---- finding 4: a stricter decision applies immediately ----

    [Fact]
    public async Task Marking_a_site_Sensitive_removes_its_thumbnail_preview_and_index_immediately()
    {
        var overrides = new Dictionary<string, DataClass>();
        var leases = new FakeLeaseManager { MaxLive = 10 };
        var thumbs = Path.Combine(Path.GetTempPath(), "jev-site-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(thumbs);
        try
        {
            var k = new TabKernel(leases, new TabRepository(_db), new CheckpointRepository(_db), thumbs, null, new WorkspaceRepository(_db),
                classifier: new DataClassifier(h => overrides.TryGetValue(h, out var c) ? c : null));
            k.Load();
            overrides["news.example.org"] = DataClass.Public;                    // the person said Public earlier: stored things are allowed
            var a = k.Open(new Uri("https://news.example.org/story"));
            await k.ActivateAsync(a.Id);
            leases[a.Id].ThumbnailToWrite = a.Id + ".png"; leases[a.Id].ThumbnailDir = thumbs;
            var b = k.Open(new Uri("https://example.net/"));
            await k.ActivateAsync(b.Id);                                          // hides a: thumbnail written
            await k.VirtualizeAsync(a.Id, Cause.User);                            // checkpoint saved
            Assert.NotNull(k.GetCheckpoint(a.Id));
            Assert.NotEmpty(Directory.GetFiles(thumbs));
            var forgotten = false;
            k.Changed += e => { if (e.Kind == "policy-tightened" && e.Id == a.Id) forgotten = true; };

            overrides["news.example.org"] = DataClass.Sensitive;                  // what the dialog does: save the decision …
            k.ReapplyPolicy();                                                    // … and apply it now

            Assert.Null(k.GetCheckpoint(a.Id)?.ThumbnailPath);                    // Sensitive keeps the scroll position, never a picture
            Assert.DoesNotContain(Directory.GetFiles(thumbs), f => Path.GetFileName(f).StartsWith(a.Id.ToString()));
            Assert.True(forgotten, "listeners (the search index) were told to forget it");
            Assert.Contains(k.Tabs, t => t.Id == b.Id);                          // other sites are untouched
        }
        finally { try { Directory.Delete(thumbs, true); } catch (IOException) { } }
    }
}
