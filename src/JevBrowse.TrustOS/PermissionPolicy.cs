namespace JevBrowse.TrustOS;

/// <summary>Browser permission kinds JevBrowse gates (Table A.7).</summary>
/// <remarks>
/// Values are stored in the database as integers, so existing members keep their numbers and new ones are appended.
/// <see cref="Other"/> (7) is only ever "a permission we could not name" and is never remembered.
/// </remarks>
public enum PermissionKind
{
    Geolocation, Camera, Microphone, Notifications, Clipboard, Midi, Sensors, Other,
    AutomaticDownloads, FileAccess, LocalFonts, Autoplay, WindowManagement, MidiSystemExclusive,
}

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

    /// <summary>
    /// A grant is stored per kind, so it is only sound for a permission we can name exactly. Everything the engine
    /// asks that we cannot name maps to <see cref="PermissionKind.Other"/>, and one remembered answer for "Other"
    /// would cover every unrelated permission from that site. So it is asked every time and never stored.
    /// </summary>
    public static bool MayRemember(PermissionKind kind) => kind != PermissionKind.Other;

    public PermissionVerdict Decide(string site, PermissionKind kind)
    {
        // Rows written before kinds were split may exist under Other; they are ignored, not honoured.
        var g = MayRemember(kind) ? _lookup(site, kind) : null;
        if (g is not null && (g.ExpiresAt is null || g.ExpiresAt > _clock()))
            return g.Allowed ? PermissionVerdict.Allow : PermissionVerdict.Deny;

        return kind switch
        {
            PermissionKind.Notifications => PermissionVerdict.Deny,   // quiet by default
            PermissionKind.Geolocation => PermissionVerdict.Ask,
            PermissionKind.Camera or PermissionKind.Microphone => PermissionVerdict.Ask,
            PermissionKind.Clipboard => PermissionVerdict.Ask,
            PermissionKind.Midi or PermissionKind.MidiSystemExclusive or PermissionKind.Sensors => PermissionVerdict.Deny,
            _ => PermissionVerdict.Ask,
        };
    }
}
