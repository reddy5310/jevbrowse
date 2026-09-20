using JevBrowse.Domain;

namespace JevBrowse.TrustOS;

/// <summary>
/// Identity of a permission grant: WHO (container, and for ephemeral containers the specific workspace) and WHERE
/// (exact origin: scheme, host, port). A grant given to https://a.example.com:443 in Work never applies to
/// http://a.example.com, to another port, to a sibling subdomain, or to the same site in Personal.
/// </summary>
public static class PermissionKey
{
    public static string For(IdentityContainer container, ContextId isolation, Uri origin)
    {
        var who = container.IsEphemeral() ? $"{container}:{isolation}" : container.ToString();
        var where = $"{origin.Scheme}://{origin.Host.ToLowerInvariant()}:{origin.Port}";
        return $"{who}|{where}";
    }

    /// <summary>Ephemeral identities must not leave grants on disk.</summary>
    public static bool MayPersist(IdentityContainer container) => !container.IsEphemeral();
}
