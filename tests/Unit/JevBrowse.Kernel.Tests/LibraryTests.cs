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
