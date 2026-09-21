namespace JevBrowse.Domain;

/// <summary>A search provider the address bar can use. <see cref="Template"/> holds {q} where the escaped query goes.</summary>
public sealed record SearchEngine(string Id, string Name, string Template)
{
    public Uri For(string query) => new(Template.Replace("{q}", Uri.EscapeDataString(query), StringComparison.Ordinal));
}

/// <summary>The built-in choices. A fixed list on purpose: a custom template could send what a person types to an address they never saw.</summary>
public static class SearchEngines
{
    public static readonly SearchEngine DuckDuckGo = new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={q}");

    public static readonly IReadOnlyList<SearchEngine> All =
    [
        DuckDuckGo,
        new("google", "Google", "https://www.google.com/search?q={q}"),
        new("bing", "Bing", "https://www.bing.com/search?q={q}"),
        new("brave", "Brave Search", "https://search.brave.com/search?q={q}"),
        new("startpage", "Startpage", "https://www.startpage.com/do/search?q={q}"),
        new("ecosia", "Ecosia", "https://www.ecosia.org/search?q={q}"),
    ];

    /// <summary>The engine with this id; anything unknown (an old or edited settings file) falls back to the default.</summary>
    public static SearchEngine Find(string? id) => All.FirstOrDefault(e => e.Id == id) ?? DuckDuckGo;
}
