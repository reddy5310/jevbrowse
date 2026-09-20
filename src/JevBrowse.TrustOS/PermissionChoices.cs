namespace JevBrowse.TrustOS;

/// <summary>What the user (or a failure to ask) decided about one permission request.</summary>
public enum PermissionChoice
{
    AllowOnce, AllowForHour, AllowAlways,
    /// <summary>Deny and remember: the site is refused this permission until the user says otherwise.</summary>
    Block,
    /// <summary>Deny this request and remember nothing. Used when we could not ask, on dismissal, and by unattended checks.</summary>
    BlockOnce,
}

/// <summary>Which button on the permission dialog was pressed. Escape and the close button are both <see cref="Dismissed"/>.</summary>
public enum PermissionDialogButton { Allow, Block, Dismissed }

public static class PermissionChoices
{
    /// <summary>
    /// Escape and the close button return the SAME result from the dialog, so they cannot be told apart and must mean
    /// the same thing. That has to be the safe one: refuse this request, remember nothing. A permanent decision needs
    /// its own button. (Before this, Escape after ticking "don't ask again" blocked the site for good.)
    /// </summary>
    public static PermissionChoice FromDialog(PermissionDialogButton button, bool allowForAnHour) => button switch
    {
        PermissionDialogButton.Allow => allowForAnHour ? PermissionChoice.AllowForHour : PermissionChoice.AllowOnce,
        PermissionDialogButton.Block => PermissionChoice.Block,
        _ => PermissionChoice.BlockOnce,
    };
}

/// <summary>Names and wording for permissions. Pure, so the mapping that decides what a grant covers can be tested.</summary>
public static class PermissionKinds
{
    /// <summary>
    /// Maps the engine's permission name to OUR kind. Each permission we can name has its own kind, because a grant is
    /// stored per kind: collapsing several into one meant allowing "automatic downloads" for an hour also allowed
    /// "read and write files" from the same site, without asking. Matched by name so an SDK that adds or renames a
    /// member degrades to <see cref="PermissionKind.Other"/> (never remembered) rather than failing to build.
    /// </summary>
    public static PermissionKind FromEngineName(string name) => name switch
    {
        "Geolocation" => PermissionKind.Geolocation,
        "Camera" => PermissionKind.Camera,
        "Microphone" => PermissionKind.Microphone,
        "Notifications" => PermissionKind.Notifications,
        "ClipboardRead" => PermissionKind.Clipboard,
        "OtherSensors" => PermissionKind.Sensors,
        "MidiSystemExclusiveMessages" => PermissionKind.MidiSystemExclusive,
        "MultipleAutomaticDownloads" => PermissionKind.AutomaticDownloads,
        "FileReadWrite" => PermissionKind.FileAccess,
        "LocalFonts" => PermissionKind.LocalFonts,
        "Autoplay" => PermissionKind.Autoplay,
        "WindowManagement" => PermissionKind.WindowManagement,
        _ => PermissionKind.Other,
    };

    /// <summary>What the site is asking to do, in words a person would use.</summary>
    public static string Phrase(PermissionKind k) => k switch
    {
        PermissionKind.Geolocation => "see your location",
        PermissionKind.Camera => "use your camera",
        PermissionKind.Microphone => "use your microphone",
        PermissionKind.Notifications => "show notifications",
        PermissionKind.Clipboard => "read what you have copied",
        PermissionKind.Midi => "use MIDI devices",
        PermissionKind.MidiSystemExclusive => "send low-level commands to MIDI devices",
        PermissionKind.Sensors => "use your device's motion sensors",
        PermissionKind.AutomaticDownloads => "download several files automatically",
        PermissionKind.FileAccess => "read and write files on your device",
        PermissionKind.LocalFonts => "see the fonts installed on your device",
        PermissionKind.Autoplay => "play media automatically",
        PermissionKind.WindowManagement => "manage your windows and screens",
        _ => "use a browser feature",
    };
}
