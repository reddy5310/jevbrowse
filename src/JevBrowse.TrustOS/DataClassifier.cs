using JevBrowse.Domain;

namespace JevBrowse.TrustOS;

/// <summary>
/// Deterministic data-class assignment (layer 2 of §11). Order of precedence, highest first:
///   container ephemeral → EPHEMERAL; user override per site; page signals (password/payment → SECRET);
///   URL heuristics (banking/health/payroll → SENSITIVE; login/account/mail → AUTHENTICATED); else PUBLIC.
/// Heuristics only ever raise the class; nothing here lowers a user override. Sensitive pages default to
/// less persistence, never more (Constitution rule 9).
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
        var heuristic = (DataClass)Math.Max((int)FromUrl(url), (int)(FromSignals(signals) ?? DataClass.Public));

        // An explicit user decision about a site outranks heuristics (their site, their call), but a page that is
        // actually asking for a password or card number is SECRET whatever the user said.
        if (_userOverride(Site(url.Host)) is { } over)
            return signals.HasFlag(PageSignals.PasswordField) || signals.HasFlag(PageSignals.PaymentField) ? DataClass.Secret : over;
        return heuristic;
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

    public static string Site(string host)
    {
        var labels = host.ToLowerInvariant().Split('.');
        return labels.Length <= 2 ? host.ToLowerInvariant() : string.Join('.', labels[^2..]);
    }

    private static readonly string[] SensitiveHostWords = ["bank", "banking", "hdfc", "icici", "sbi.", "axisbank", "kotak", "paypal", "payroll", "health", "medical", "patient", "insurance", "upstox", "zerodha", "dhan.", "kite.", "tax", "irs.gov", "incometax"];
    private static readonly string[] SensitivePathWords = ["/checkout", "/payment", "/billing", "/wallet", "/transfer", "/statement"];
    private static readonly string[] AuthHostPrefixes = ["mail.", "outlook.", "accounts.", "login.", "auth.", "sso.", "app.", "dashboard.", "admin.", "console."];
    private static readonly string[] AuthPathWords = ["/login", "/signin", "/sign-in", "/account", "/settings", "/inbox", "/admin", "/dashboard"];
}
