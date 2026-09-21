using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.TrustOS;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

/// <summary>History, download list and per-site zoom: what may be recorded (nothing from Private, agent or Sensitive pages), and that the stored values behave.</summary>
public class LibraryTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly Dictionary<string, DataClass> _overrides = [];
    private readonly TabKernel _k;
    private readonly HistoryRepository _history;
    private readonly HistoryRecorder _recorder;
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public LibraryTests()
    {
        _k = new TabKernel(new FakeLeaseManager { MaxLive = 6 }, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), null, new WorkspaceRepository(_db),
            classifier: new DataClassifier(h => _overrides.TryGetValue(h, out var c) ? c : null));
        _k.Load();
        _history = new HistoryRepository(_db);
        _recorder = new HistoryRecorder(_k, _history, () => _now);
    }
    public void Dispose() => _db.Dispose();

    [Fact]
    public void An_ordinary_page_is_recorded_once_per_address_with_a_count()
    {
        var t = _k.Open(new Uri("https://example.org/a"));
        Assert.True(_recorder.Record(t.Id));
        _now = _now.AddMinutes(5);
        Assert.True(_recorder.Record(t.Id));

        var v = Assert.Single(_history.List());
        Assert.Equal(2, v.Visits);
        Assert.Equal(_now, v.VisitedAt);
    }

    [Fact]
    public void A_Private_session_records_nothing()
    {
        var priv = _k.CreateWorkspace("Private", IdentityContainer.Private);
        var t = _k.OpenIn(priv.Id, new Uri("https://example.org/private-page"));
        Assert.False(_recorder.Record(t.Id));
        Assert.Empty(_history.List());
    }

    [Fact]
    public void A_Sensitive_page_records_nothing()
    {
        var t = _k.Open(new Uri("https://netbanking.hdfcbank.com/statement"));
        Assert.False(_recorder.Record(t.Id));
        var marked = _k.Open(new Uri("https://clinic.example.net/records"));
        _overrides["clinic.example.net"] = DataClass.Sensitive;
        _k.ReapplyPolicy();
        Assert.False(_recorder.Record(marked.Id));
        Assert.Empty(_history.List());
    }

    [Fact]
    public void Only_web_pages_are_recorded_and_filter_remove_and_clear_work()
    {
        var a = _k.Open(new Uri("https://example.org/alpha"));
        var b = _k.Open(new Uri("https://example.org/beta"));
        _recorder.Record(a.Id); _now = _now.AddMinutes(1); _recorder.Record(b.Id);
        _history.Record("javascript:alert(1)", "x", _now);
        _history.Record("file:///c:/x", "x", _now);

        Assert.Equal(2, _history.List().Count);
        Assert.Equal("https://example.org/beta", _history.List()[0].Url);          // newest first
        Assert.Single(_history.List("alpha"));
        Assert.True(_history.Remove("https://example.org/alpha"));
        Assert.Single(_history.List());
        Assert.Equal(1, _history.Clear());
        Assert.Empty(_history.List());
    }

    [Fact]
    public void Downloads_are_listed_newest_first_and_clearing_the_list_is_only_the_list()
    {
        var repo = new DownloadRepository(_db);
        var dir = Path.Combine(Path.GetTempPath(), "jev-dl-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "a.txt"); File.WriteAllText(file, "x");
        try
        {
            repo.Add(new DownloadRecord("a.txt", file, "example.org", _now, true));
            repo.Add(new DownloadRecord("b.bin", Path.Combine(dir, "b.bin"), "example.org", _now.AddMinutes(1), false));
            Assert.Equal(["b.bin", "a.txt"], repo.List().Select(d => d.Name));
            Assert.False(repo.List()[0].Completed);
            repo.Clear();
            Assert.Empty(repo.List());
            Assert.True(File.Exists(file));                                        // the file itself is never touched
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(1.0, 1, 1.1)]
    [InlineData(1.0, -1, 0.9)]
    [InlineData(1.1, 1, 1.25)]
    [InlineData(5.0, 1, 5.0)]
    [InlineData(0.25, -1, 0.25)]
    [InlineData(double.NaN, 1, 1.1)]
    [InlineData(-3, -1, 0.9)]
    public void Zoom_steps_along_the_familiar_ladder_and_never_leaves_its_range(double from, int dir, double expected) => Assert.Equal(expected, ZoomLevels.Step(from, dir));

    [Fact]
    public void Zoom_is_remembered_per_exact_host_and_a_corrupt_value_reads_as_100_percent()
    {
        var z = new SiteZoomRepository(_db);
        Assert.Equal(1.0, z.Get("example.org"));
        z.Set("example.org", 1.5);
        Assert.Equal(1.5, z.Get("EXAMPLE.org"));
        Assert.Equal(1.0, z.Get("other.example.org"));                             // another host is not affected
        z.Set("example.org", 1.0);
        Assert.Equal(1.0, z.Get("example.org"));                                   // back to 100% stores nothing
        using (var cmd = _db.Connection.CreateCommand()) { cmd.CommandText = "INSERT INTO site_zoom VALUES ('bad.example', 99999)"; cmd.ExecuteNonQuery(); }
        Assert.Equal(1.0, z.Get("bad.example"));
    }
}

/// <summary>Corrective pass after review: what is stored must follow a class change, download ownership must never default to disk, and one data folder has one owner.</summary>
public class LibraryPolicyTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly Dictionary<string, DataClass> _overrides = [];
    private readonly TabKernel _k;
    private readonly HistoryRepository _history;
    private readonly SiteZoomRepository _zoom;
    private readonly HistoryRecorder _recorder;

    public LibraryPolicyTests()
    {
        _k = new TabKernel(new FakeLeaseManager { MaxLive = 6 }, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), null, new WorkspaceRepository(_db),
            classifier: new DataClassifier(h => _overrides.TryGetValue(h, out var c) ? c : null));
        _k.Load();
        _history = new HistoryRepository(_db); _zoom = new SiteZoomRepository(_db);
        _recorder = new HistoryRecorder(_k, _history, null, _zoom);
    }
    public void Dispose() => _db.Dispose();

    [Fact]
    public void Marking_a_site_Sensitive_removes_its_history_and_zoom_and_leaves_other_sites_alone()
    {
        var a = _k.Open(new Uri("https://clinic.example.net/records")); var b = _k.Open(new Uri("https://example.org/keep"));
        _recorder.Record(a.Id); _recorder.Record(b.Id);
        _zoom.Set("clinic.example.net", 1.5); _zoom.Set("example.org", 1.25);

        _overrides["clinic.example.net"] = DataClass.Sensitive;   // the decision alone; the sweep is what must clean up
        var removed = _recorder.Sweep();

        Assert.Equal(1, removed);
        Assert.Equal(["example.org"], _history.List().Select(v => v.Host));
        Assert.Equal(1.0, _zoom.Get("clinic.example.net"));
        Assert.Equal(1.25, _zoom.Get("example.org"));
    }

    [Fact]
    public void A_page_that_turns_Sensitive_after_it_was_recorded_takes_its_saved_history_and_zoom_with_it()
    {
        var t = _k.Open(new Uri("https://shop.example.org/cart"));
        _recorder.Record(t.Id); _zoom.Set("shop.example.org", 1.5);
        Assert.Single(_history.List());

        Assert.True(_k.RaiseClass(t.Id, DataClass.Sensitive));                    // e.g. a card field appeared

        Assert.Empty(_history.List());
        Assert.Equal(1.0, _zoom.Get("shop.example.org"));
    }

    [Fact]
    public void A_Private_tab_on_the_same_site_does_not_erase_what_an_ordinary_tab_left()
    {
        var ordinary = _k.Open(new Uri("https://example.org/a")); _recorder.Record(ordinary.Id);
        var priv = _k.CreateWorkspace("Private", IdentityContainer.Private);
        var p = _k.OpenIn(priv.Id, new Uri("https://example.org/b"));
        _k.RaiseClass(p.Id, DataClass.Secret);
        Assert.Single(_history.List());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(IdentityContainer.Private, false)]
    [InlineData(IdentityContainer.Disposable, false)]
    [InlineData(IdentityContainer.Personal, true)]
    [InlineData(IdentityContainer.Work, true)]
    public void A_download_is_written_to_disk_only_when_its_owner_is_known_and_ordinary(IdentityContainer? owner, bool persists) =>
        Assert.Equal(persists, DownloadOwnership.MayPersist(owner));

    [Fact]
    public void Startup_and_storage_share_one_data_folder_answer()
    {
        var local = Path.Combine(Path.GetTempPath(), "jev-local");
        Assert.Equal(Path.GetFullPath(Path.Combine(local, "JevBrowse")), DataLocation.Resolve(null, local));
        Assert.Equal(Path.GetFullPath(Path.Combine(local, "JevBrowse")), DataLocation.Resolve("  ", local));
        var custom = Path.Combine(Path.GetTempPath(), "jev-custom");
        Assert.Equal(Path.GetFullPath(custom), DataLocation.Resolve(custom, local));
        Assert.Equal(DataLocation.Resolve(null, local), DataLocation.Resolve(null, local));   // independent of where the program was extracted
    }

    [Fact]
    public void A_data_folder_has_one_owner_at_a_time()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jev-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = InstanceLock.TryAcquire(dir);
            Assert.NotNull(first);
            Assert.Null(InstanceLock.TryAcquire(dir));                             // a second copy is refused while the first runs
            first!.Dispose();
            using var again = InstanceLock.TryAcquire(dir);
            Assert.NotNull(again);                                                 // and the lock is released when the owner goes
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
