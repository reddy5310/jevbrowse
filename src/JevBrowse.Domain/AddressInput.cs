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

    public static AddressResult Resolve(string? text)
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
        return new(AddressKind.Search, new Uri(SearchUrl + Uri.EscapeDataString(t)));
    }
}
