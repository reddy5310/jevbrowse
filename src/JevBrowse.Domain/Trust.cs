namespace JevBrowse.Domain;

/// <summary>
/// Table A.10. Higher = more sensitive. Persistence and AI defaults get stricter as the class rises.
/// <para>
/// <see cref="Unknown"/> is the default for a page we have no positive evidence about: it is NOT a claim that the
/// page is public. PUBLIC must be earned by a scoped rule (a structurally public host) or an informed user choice
/// for that site. Numeric order matters — the policy matrix and every Math.Max combination depend on it, and stored
/// user overrides are migrated when it changes (BrowserDb v8).
/// </para>
/// </summary>
public enum DataClass { Public = 0, Unknown = 1, Authenticated = 2, Sensitive = 3, Secret = 4, Ephemeral = 5 }

public static class DataClassExtensions
{
    /// <summary>
    /// Classes an explicit user action may extend a cloud call to, when Trust OS alone would not (Table A.8).
    /// <para>
    /// Enumerated deliberately, never <c>cls &lt;= Authenticated</c>: an ordinal test silently widens what may leave
    /// the device the moment a class is inserted into the enum, which is exactly how UNKNOWN became sendable when
    /// <see cref="DataClass.Unknown"/> was added at 1. A page we could not assess is not a page the user can
    /// knowingly consent to send, so it is not on this list.
    /// </para>
    /// </summary>
    public static bool MayLeaveDeviceOnExplicitRequest(this DataClass c) => c is DataClass.Public or DataClass.Authenticated;
}

/// <summary>§10.1 identity containers. Each maps to its own WebView2 user-data folder (cookies/storage/permissions).</summary>
public enum IdentityContainer { Personal, Work, Dev, Disposable, Private }

public static class IdentityContainerExtensions
{
    /// <summary>Private and Disposable leave nothing durable: no tab rows, no checkpoints, no index.</summary>
    public static bool IsEphemeral(this IdentityContainer c) => c is IdentityContainer.Private or IdentityContainer.Disposable;
}

/// <summary>Operations Trust OS gates. Adding one here forces every policy to decide on it.</summary>
public enum DataOperation
{
    PersistTabRow,        // durable URL/title/ordinal
    PersistCheckpoint,    // scroll, favicon
    PersistThumbnail,
    IndexContent,         // Browser Memory (Phase 8)
    SendToCloudAI,        // Phase 7
    ExposeToAgent,        // Phase 10
}

public sealed record TrustDecision(bool Allowed, string Reason)
{
    public static TrustDecision Allow(string why) => new(true, why);
    public static TrustDecision Deny(string why) => new(false, why);
}

/// <summary>Signals the classifier reads. Detected ones come from the live page through the narrow bridge.</summary>
[Flags]
public enum PageSignals
{
    None = 0,
    PasswordField = 1 << 0,   // a password input exists on the page
    PaymentField = 1 << 1,    // autocomplete=cc-number etc.
    Authenticated = 1 << 2,   // heuristics: logout link / account menu; conservative
    /// <summary>
    /// The page has real rendered content (not an empty app shell) and shows no sign-in affordance. This says the
    /// page is <i>readable</i>, not that it is <i>public</i>: an authenticated document satisfies it exactly as a
    /// public article does. It therefore never promotes a class — it only tells the UI the assessment is settled
    /// rather than still loading, so "Not assessed" is not shown for a page that simply has not rendered yet.
    /// </summary>
    ContentRendered = 1 << 3,
}
