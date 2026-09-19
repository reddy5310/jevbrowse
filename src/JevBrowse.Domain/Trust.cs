namespace JevBrowse.Domain;

/// <summary>Table A.10. Higher = more sensitive. Persistence and AI defaults get stricter as the class rises.</summary>
public enum DataClass { Public, Authenticated, Sensitive, Secret, Ephemeral }

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
}
