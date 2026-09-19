namespace JevBrowse.Domain;

/// <summary>Conditions that veto automatic demotion (Architecture §19: "any unsafe/protected condition can veto").</summary>
[Flags]
public enum ProtectionFlags
{
    None = 0,
    Audible = 1 << 0,
    WebRtcActive = 1 << 1,
    DownloadActive = 1 << 2,
    DirtyForm = 1 << 3,
    UserPinned = 1 << 4,
    NeverHibernateSite = 1 << 5,
}
