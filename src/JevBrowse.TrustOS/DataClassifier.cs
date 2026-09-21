using JevBrowse.Domain;

namespace JevBrowse.TrustOS;

/// <summary>
/// Deterministic data-class assignment (layer 2 of §11). Order of precedence, highest first:
///   container ephemeral → EPHEMERAL; user override per site; page signals (password/payment → SECRET);
///   URL heuristics (banking/health/payroll → SENSITIVE; login/account/mail → AUTHENTICATED); else UNKNOWN,
///   unless the host is structurally public.
/// Heuristics only ever raise the class; nothing here lowers a user override. PUBLIC is never inferred from the
/// absence of evidence. Sensitive pages default to less persistence, never more (Constitution rule 9).
/// </summary>
public sealed class DataClassifier
{
    private readonly Func<string, DataClass?> _userOverride;

    public DataClassifier(Func<string, DataClass?>? userOverrideBySite = null) => _userOverride = userOverrideBySite ?? (_ => null);

    public DataClass Classify(Uri url, IdentityContainer container, PageSignals signals)
    {
        if (container.IsEphemeral()) return DataClass.Ephemeral;

        // Independent evidence is combined by taking the STRICTER result: a "logged in" signal must never mask a
        // banking URL, and a banking URL must never mask a password field.
        var known = (DataClass)Math.Max((int)FromUrl(url), (int)(FromSignals(signals) ?? DataClass.Public));

        // An explicit user decision about a site outranks heuristics (their site, their call), but a page that is
        // actually asking for a password or card number is SECRET whatever the user said.
        if (_userOverride(HostKey(url.Host)) is { } over)
            return signals.HasFlag(PageSignals.PasswordField) || signals.HasFlag(PageSignals.PaymentField) ? DataClass.Secret : over;

        if (known > DataClass.Public) return known;     // something concrete points at sensitivity

        // Nothing does. That is not the same as knowing the page is public. Absence of a sign-in affordance is not
        // evidence of public availability — an authenticated document or single-page app satisfies it just as well —
        // so no page-side signal promotes to PUBLIC. Only a structurally public host does; everything else is
        // UNKNOWN until the user decides for that site (the class badge writes the override).
        return IsKnownPublic(url) ? DataClass.Public : DataClass.Unknown;
    }

    private static DataClass? FromSignals(PageSignals s)
    {
        if (s.HasFlag(PageSignals.PasswordField) || s.HasFlag(PageSignals.PaymentField)) return DataClass.Secret;
        if (s.HasFlag(PageSignals.Authenticated)) return DataClass.Authenticated;
        return null;
    }

    private static DataClass FromUrl(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        var path = url.AbsolutePath.ToLowerInvariant();
        if (SensitiveHostWords.Any(w => host.Contains(w)) || SensitivePathWords.Any(w => path.Contains(w))) return DataClass.Sensitive;
        if (AuthHostPrefixes.Any(p => host.StartsWith(p)) || AuthPathWords.Any(w => path.Contains(w))) return DataClass.Authenticated;
        if (url.Scheme == "http" && host is "localhost" or "127.0.0.1") return DataClass.Authenticated; // dev servers are rarely public
        return DataClass.Public;
    }

    /// <summary>
    /// URLs whose public nature is structural rather than guessed: reference and documentation sites that serve the
    /// same content to everyone, signed in or not. Deliberately short, and the only automatic route to PUBLIC —
    /// every other site stays UNKNOWN until its owner tells us otherwise.
    /// </summary>
    private static bool IsKnownPublic(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        return KnownPublicSuffixes.Any(s => host == s || host.EndsWith("." + s, StringComparison.Ordinal));
    }

    private static readonly string[] KnownPublicSuffixes =
        ["wikipedia.org", "wikimedia.org", "wiktionary.org", "learn.microsoft.com", "developer.mozilla.org", "docs.python.org", "w3.org", "rfc-editor.org", "gnu.org", "archive.org"];

    /// <summary>
    /// The key a per-site decision is stored under: the EXACT host (lower-cased, without a trailing dot). It used to be the last two labels, which made
    /// one.co.uk and two.co.uk both "co.uk" and every github.io page one site, so marking one Public loosened unrelated sites. Without a public-suffix
    /// list the only safe key is the host the person was actually looking at.
    /// </summary>
    public static string HostKey(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    private static readonly string[] SensitiveHostWords = ["bank", "banking", "hdfc", "icici", "sbi.", "axisbank", "kotak", "paypal", "payroll", "health", "medical", "patient", "insurance", "upstox", "zerodha", "dhan.", "kite.", "tax", "irs.gov", "incometax"];
    private static readonly string[] SensitivePathWords = ["/checkout", "/payment", "/billing", "/wallet", "/transfer", "/statement"];
    private static readonly string[] AuthHostPrefixes = ["mail.", "outlook.", "accounts.", "login.", "auth.", "sso.", "app.", "dashboard.", "admin.", "console."];
    private static readonly string[] AuthPathWords = ["/login", "/signin", "/sign-in", "/account", "/settings", "/inbox", "/admin", "/dashboard"];
}
