namespace JevBrowse.Domain;

/// <summary>
/// Conditions that veto automatic demotion (Architecture §19: "any unsafe/protected condition can veto").
/// <para>
/// Everything here is about <i>staying awake</i>. Where a tab sits in the list is <see cref="VirtualTab.IsPinned"/>,
/// deliberately not a flag in this enum: pinning a tab for easy reach must not quietly exempt it from sleeping,
/// which is what the old combined "pin" control did.
/// </para>
/// </summary>
[Flags]
public enum ProtectionFlags
{
    None = 0,
    Audible = 1 << 0,
    WebRtcActive = 1 << 1,
    DownloadActive = 1 << 2,
    DirtyForm = 1 << 3,
    /// <summary>
    /// The user asked for this tab never to sleep on its own. Keeps bit 4, which the old <c>UserPinned</c> flag
    /// used for exactly this meaning, so every stored preference carries over untouched — a rename in the code,
    /// not a migration on disk.
    /// </summary>
    KeepActive = 1 << 4,
    NeverHibernateSite = 1 << 5,
    /// <summary>The microphone is open. Separate from the others because a person needs to know which one it is.</summary>
    MicrophoneActive = 1 << 6,
    CameraActive = 1 << 7,
    ScreenShareActive = 1 << 8,
}

public static class ProtectionFlagsExtensions
{
    /// <summary>
    /// Live capture or a call: the things that are destroyed, not merely paused, by disposing the renderer. Used
    /// where the difference matters to a person — "you are sharing your screen" is not the same warning as
    /// "a download is running".
    /// </summary>
    public const ProtectionFlags LiveMedia =
        ProtectionFlags.MicrophoneActive | ProtectionFlags.CameraActive | ProtectionFlags.ScreenShareActive | ProtectionFlags.WebRtcActive;

    public static bool HasLiveMedia(this ProtectionFlags f) => (f & LiveMedia) != 0;
}
