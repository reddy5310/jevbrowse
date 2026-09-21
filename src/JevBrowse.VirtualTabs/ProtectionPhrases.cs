using JevBrowse.Domain;

namespace JevBrowse.VirtualTabs;

/// <summary>
/// The reasons a tab is being kept awake, in words a person would use. One list, shared by the tab row and the Receipt,
/// so the two cannot describe the same tab differently. Placement (pinned) is deliberately not here: pinning decides
/// where a tab sits, not whether it sleeps.
/// </summary>
public static class ProtectionPhrases
{
    public static IReadOnlyList<string> StayAwake(ProtectionFlags p)
    {
        var reasons = new List<string>();
        if (p.HasFlag(ProtectionFlags.KeepActive)) reasons.Add("kept active");
        if (p.HasFlag(ProtectionFlags.NeverHibernateSite)) reasons.Add("site kept active");
        if (p.HasFlag(ProtectionFlags.Audible)) reasons.Add("playing sound");
        // Named separately: "sharing your screen" and "using your microphone" are different things to be told.
        if (p.HasFlag(ProtectionFlags.ScreenShareActive)) reasons.Add("sharing screen");
        if (p.HasFlag(ProtectionFlags.CameraActive)) reasons.Add("using camera");
        if (p.HasFlag(ProtectionFlags.MicrophoneActive)) reasons.Add("using microphone");
        if (p.HasFlag(ProtectionFlags.WebRtcActive)) reasons.Add("call active");
        if (p.HasFlag(ProtectionFlags.DownloadActive)) reasons.Add("downloading");
        if (p.HasFlag(ProtectionFlags.UploadActive)) reasons.Add("uploading");
        if (p.HasFlag(ProtectionFlags.VideoPlaying)) reasons.Add("playing video");
        if (p.HasFlag(ProtectionFlags.DirtyForm)) reasons.Add("unsaved typing");
        return reasons;
    }
}
