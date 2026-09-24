namespace JevBrowse.Domain;

public enum AddressKind { Navigate, Search, Invalid }

/// <param name="Url">Where to go (Navigate) or the search to run (Search). Null when Invalid.</param>
/// <param name="Message">Why it was refused (Invalid), in words a person can act on.</param>
public sealed record AddressResult(AddressKind Kind, Uri? Url, string? Message = null);

/// <summary>
/// What the address bar does with what was typed. Pure, so every odd input can be tested: it must never throw, never navigate somewhere the browser does not
/// support, and never turn a half-typed address into a crash.
/// </summary>
public static class AddressInput
{
    public const string SearchUrl = "https://duckduckgo.com/?q=";

    public static AddressResult Resolve(string? text, SearchEngine? engine = null)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return new(AddressKind.Invalid, null, "");
        if (t.Length > 8192) return new(AddressKind.Invalid, null, "That address is too long.");

        if (t.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host))
                return new(AddressKind.Invalid, null, "That is not a complete web address. Add the site name after \"" + t.Split("://")[0] + "://\".");
            if (u.Scheme is "http" or "https") return new(AddressKind.Navigate, u);
            if (u.Scheme == "jev" && u.Host == "welcome") return new(AddressKind.Navigate, u);
            return new(AddressKind.Invalid, null, "JevBrowse opens web addresses (http and https) from the address bar.");
        }

        // No scheme. Something that looks like a host is one; anything with a space, or without a dot, is a search.
        if (!t.Contains(' '))
        {
            var local = t.StartsWith("localhost", StringComparison.OrdinalIgnoreCase);
            if (local || t.Contains('.'))
            {
                if (Uri.TryCreate((local ? "http://" : "https://") + t, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host) && u.Scheme is "http" or "https")
                    return new(AddressKind.Navigate, u);
            }
        }
        return new(AddressKind.Search, (engine ?? SearchEngines.DuckDuckGo).For(t));
    }

    /// <summary>
    /// Ctrl+Enter: the person is explicitly saying "this is a site name", the same convention as other browsers. A bare word like <c>example</c> becomes
    /// <c>https://www.example.com</c>; something that already has a scheme, already has a dot, or already starts with <c>www.</c> is left as it is and
    /// just forced to Navigate instead of falling through to a search. Never used for anything containing a space: that is unambiguously a search, Ctrl or not.
    /// </summary>
    public static AddressResult ResolveForceNavigate(string? text, SearchEngine? engine = null)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Contains(' ') || t.Contains("://", StringComparison.Ordinal)
            || t.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
            return Resolve(text, engine);   // an explicit scheme, empty text, a space, or localhost: the ordinary rules already do the right thing

        var host = t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? t : "www." + t;
        var firstDot = host.IndexOf('.', StringComparison.Ordinal);
        if (host.IndexOf('.', firstDot + 1) < 0) host += ".com";   // no second dot after "www." means no TLD yet

        if (Uri.TryCreate("https://" + host, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
            return new(AddressKind.Navigate, u);
        return Resolve(text, engine);
    }
}
