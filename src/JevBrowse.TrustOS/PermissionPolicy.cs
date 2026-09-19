namespace JevBrowse.TrustOS;

/// <summary>Browser permission kinds JevBrowse gates (Table A.7).</summary>
public enum PermissionKind { Geolocation, Camera, Microphone, Notifications, Clipboard, Midi, Sensors, Other }

public enum PermissionVerdict { Allow, Deny, Ask }

/// <summary>A grant with optional expiry. `null` expiry = always.</summary>
public sealed record PermissionGrant(string Site, PermissionKind Kind, bool Allowed, DateTimeOffset? ExpiresAt);

/// <summary>
/// Default principles from Table A.7, then stored grants. Temporary grants ("allow for 1 hour") are first-class.
/// Storage is injected so this stays a pure decision table.
/// </summary>
public sealed class PermissionPolicy
{
    private readonly Func<string, PermissionKind, PermissionGrant?> _lookup;
    private readonly Func<DateTimeOffset> _clock;

    public PermissionPolicy(Func<string, PermissionKind, PermissionGrant?> lookup, Func<DateTimeOffset>? clock = null)
    {
        _lookup = lookup;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public PermissionVerdict Decide(string site, PermissionKind kind)
    {
        var g = _lookup(site, kind);
        if (g is not null && (g.ExpiresAt is null || g.ExpiresAt > _clock()))
            return g.Allowed ? PermissionVerdict.Allow : PermissionVerdict.Deny;

        return kind switch
        {
            PermissionKind.Notifications => PermissionVerdict.Deny,   // quiet by default
            PermissionKind.Geolocation => PermissionVerdict.Ask,
            PermissionKind.Camera or PermissionKind.Microphone => PermissionVerdict.Ask,
            PermissionKind.Clipboard => PermissionVerdict.Ask,
            PermissionKind.Midi or PermissionKind.Sensors => PermissionVerdict.Deny,
            _ => PermissionVerdict.Ask,
        };
    }
}
