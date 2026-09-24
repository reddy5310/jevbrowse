using JevBrowse.Domain;
using JevBrowse.Storage;

namespace JevBrowse.Kernel.Tests;

public class BookmarkAndSearchTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    public void Dispose() => _db.Dispose();

    private const string Export = """
        <!DOCTYPE NETSCAPE-Bookmark-file-1>
        <DL><p>
          <DT><H3>Bookmarks bar</H3>
          <DL><p>
            <DT><A HREF="https://example.org/a?x=1&amp;y=2" ADD_DATE="1600000000">Example &amp; A</A>
            <DT><H3>Work</H3>
            <DL><p>
              <DT><A HREF="https://example.org/w" ADD_DATE="1600000100">Work page</A>
            </DL><p>
            <DT><A HREF="javascript:alert(1)">Bad</A>
            <DT><A HREF="file:///c:/secret.txt">Bad file</A>
            <DT><A HREF="ftp://files.example.org/x">Bad ftp</A>
            <DT><A HREF="chrome://settings">Bad chrome</A>
            <DT><A HREF="https://example.org/a?x=1&amp;y=2">Duplicate</A>
          </DL><p>
        </DL><p>
        """;

    [Fact]
    public void An_exported_bookmark_file_is_read_with_folders_and_only_web_addresses_are_kept()
    {
        var list = BookmarkImport.Parse(Export);
        Assert.Equal(2, list.Count);                                             // javascript:, file: and the duplicate are dropped
        Assert.Equal("https://example.org/a?x=1&y=2", list[0].Url);
        Assert.Equal("Example & A", list[0].Title);
        Assert.Equal("Bookmarks bar", list[0].Folder);
        Assert.Equal("Bookmarks bar / Work", list[1].Folder);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1600000000), list[0].AddedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<A HREF=\"https://")]
    [InlineData("not html at all \0\0")]
    public void A_damaged_or_empty_file_gives_nothing_and_never_throws(string? text) => Assert.Empty(BookmarkImport.Parse(text));

    [Fact]
    public void Saving_twice_does_not_duplicate_and_an_import_keeps_what_the_person_already_has()
    {
        var repo = new BookmarkRepository(_db);
        repo.Save(new Bookmark("https://example.org/a", "My name for it"));
        var added = repo.SaveMany([new Bookmark("https://example.org/a", "Imported name"), new Bookmark("https://example.org/b", "B")]);

        Assert.Equal(1, added);
        Assert.Equal("My name for it", repo.List().Single(b => b.Url.EndsWith("/a")).Title);
        Assert.True(repo.Contains("https://example.org/b"));
        Assert.True(repo.Remove("https://example.org/b"));
        Assert.False(repo.Contains("https://example.org/b"));
    }

    [Fact]
    public void The_list_can_be_filtered_by_title_address_or_folder_as_plain_text()
    {
        var repo = new BookmarkRepository(_db);
        repo.SaveMany(BookmarkImport.Parse(Export));
        Assert.Single(repo.List("work page"));
        Assert.Equal(2, repo.List("EXAMPLE.ORG").Count);
        Assert.Empty(repo.List(".*"));                                            // not a pattern
    }

    [Fact]
    public void Bookmarks_survive_reopening_the_database()
    {
        var path = Path.Combine(Path.GetTempPath(), "jev-bm-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new BrowserDb(path)) new BookmarkRepository(db).Save(new Bookmark("https://example.org/k", "Kept"));
            using var again = new BrowserDb(path);
            Assert.True(new BookmarkRepository(again).Contains("https://example.org/k"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) try { File.Delete(f); } catch (IOException) { } }
    }

    [Fact]
    public void A_chosen_search_engine_is_used_and_an_unknown_one_falls_back_to_the_default()
    {
        var g = AddressInput.Resolve("best pizza & pasta", SearchEngines.Find("google"));
        Assert.Equal(AddressKind.Search, g.Kind);
        Assert.StartsWith("https://www.google.com/search?q=best%20pizza%20%26%20pasta", g.Url!.AbsoluteUri);
        Assert.StartsWith(AddressInput.SearchUrl, AddressInput.Resolve("hello world", SearchEngines.Find("nonsense")).Url!.ToString());
        Assert.StartsWith(AddressInput.SearchUrl, AddressInput.Resolve("hello world").Url!.ToString());
        Assert.Equal(AddressKind.Navigate, AddressInput.Resolve("example.org", SearchEngines.Find("bing")).Kind);   // an address is still an address
    }

    [Fact]
    public void Every_built_in_engine_is_an_https_address_that_carries_the_query()
    {
        foreach (var e in SearchEngines.All)
        {
            var u = e.For("a b");
            Assert.Equal("https", u.Scheme);
            Assert.Contains("a%20b", u.AbsoluteUri);
        }
    }

    [Fact]
    public void Exported_bookmarks_round_trip_through_import()
    {
        var original = new[]
        {
            new Bookmark("https://example.org/a", "Example & \"quoted\" <tag>", "", DateTimeOffset.FromUnixTimeSeconds(1600000000)),
            new Bookmark("https://example.org/w", "Work page", "Work", DateTimeOffset.FromUnixTimeSeconds(1600000100)),
        };
        var html = BookmarkExport.ToHtml(original);
        var reimported = BookmarkImport.Parse(html);

        Assert.Equal(2, reimported.Count);
        Assert.Contains(reimported, b => b.Url == "https://example.org/a" && b.Title == "Example & \"quoted\" <tag>");
        Assert.Contains(reimported, b => b.Url == "https://example.org/w" && b.Folder == "Work");
    }

    [Fact]
    public void Exported_html_never_contains_an_unescaped_special_character_from_the_title()
    {
        var html = BookmarkExport.ToHtml([new Bookmark("https://example.org/x", "<script>alert(1)</script> & \"more\"")]);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Export_and_import_agree_end_to_end_through_the_repository()
    {
        var repo = new BookmarkRepository(_db);
        repo.SaveMany(BookmarkImport.Parse(Export));
        var html = BookmarkExport.ToHtml(repo.List());
        var reimported = BookmarkImport.Parse(html);
        Assert.Equal(repo.List().Count, reimported.Count);
        Assert.All(repo.List(), original => Assert.Contains(reimported, r => r.Url == original.Url));
    }
}
