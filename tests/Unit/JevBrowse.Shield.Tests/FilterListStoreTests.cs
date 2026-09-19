using System.Net;
using JevBrowse.Shield;

namespace JevBrowse.Shield.Tests;

public class FilterListStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jev-filters-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(respond(r.RequestUri!));
    }

    private static string GoodList(int n, string tag) => "[Adblock Plus 2.0]\n! comment\n" + string.Join('\n', Enumerable.Range(0, n).Select(i => $"||{tag}{i}.example.net^"));

    [Fact]
    public async Task Valid_download_is_activated_and_previous_kept()
    {
        var store = new FilterListStore(_root);
        var src = new[] { new FilterListStore.ListSource("a", new Uri("https://lists.test/a.txt")) };

        var http1 = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GoodList(1500, "v1-")) }));
        var r1 = await store.UpdateAsync(http1, src);
        Assert.True(r1.Activated);
        Assert.True(store.HasActiveLists);
        Assert.Equal(1500, FilterEngine.Compile(store.ReadActiveLines()).RuleCount);

        var http2 = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GoodList(2000, "v2-")) }));
        Assert.True((await store.UpdateAsync(http2, src)).Activated);
        Assert.Equal(2000, FilterEngine.Compile(store.ReadActiveLines()).RuleCount);
        Assert.True(Directory.Exists(store.PreviousDir));

        Assert.True(store.Rollback());
        Assert.Equal(1500, FilterEngine.Compile(store.ReadActiveLines()).RuleCount);
    }

    [Fact]
    public async Task Bad_download_never_touches_active()
    {
        var store = new FilterListStore(_root);
        var src = new[] { new FilterListStore.ListSource("a", new Uri("https://lists.test/a.txt")) };
        var good = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GoodList(1500, "ok-")) }));
        Assert.True((await store.UpdateAsync(good, src)).Activated);

        var tooSmall = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GoodList(10, "tiny-")) }));
        var r = await store.UpdateAsync(tooSmall, src);
        Assert.False(r.Activated);
        Assert.Contains("rejected", r.Details["a"]);

        var http500 = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        Assert.False((await store.UpdateAsync(http500, src)).Activated);

        Assert.Equal(1500, FilterEngine.Compile(store.ReadActiveLines()).RuleCount); // still the good one
    }
}
