using System.ComponentModel;
using System.Runtime.CompilerServices;
using JevBrowse.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace JevBrowse.App;

/// <summary>Sidebar view of a VirtualTab. Presentation only; the kernel owns the truth.</summary>
public sealed class TabItem(VirtualTab tab) : INotifyPropertyChanged
{
    private static readonly Dictionary<ResourceState, SolidColorBrush> Brushes = new()
    {
        [ResourceState.Hot] = new(Windows.UI.Color.FromArgb(255, 0x3D, 0xDC, 0x84)),
        [ResourceState.Warm] = new(Windows.UI.Color.FromArgb(255, 0xFF, 0xB4, 0x54)),
        [ResourceState.Cold] = new(Windows.UI.Color.FromArgb(255, 0x4A, 0xA3, 0xFF)),
        [ResourceState.Suspended] = new(Windows.UI.Color.FromArgb(255, 0x8B, 0x7B, 0xFF)),
        [ResourceState.Virtual] = new(Windows.UI.Color.FromArgb(255, 0x6B, 0x73, 0x85)),
        [ResourceState.Archived] = new(Windows.UI.Color.FromArgb(255, 0x3A, 0x3F, 0x4B)),
    };

    public VirtualTab Tab { get; } = tab;
    public ResourceId Id => Tab.Id;
    public string Title => Tab.Url.Scheme == "jev" ? "Welcome to JevBrowse" : string.IsNullOrWhiteSpace(Tab.Title) ? Tab.Url.Host : Tab.Title;
    public string Subtitle => string.Join(" • ", new[] { StateWord, ProtectionWords, Tab.Url.Scheme == "jev" ? "local page" : Tab.Url.Host }.Where(s => s.Length > 0));

    /// <summary>What the tab is doing, in the words a person would use. Internal state names are for diagnostics.</summary>
    private string StateWord => Tab.State switch
    {
        ResourceState.Hot => "open",
        ResourceState.Warm => "open, in the background",
        ResourceState.Cold => "idle",
        ResourceState.Suspended => "dozing",
        ResourceState.Virtual => "sleeping",
        ResourceState.Archived => "archived",
        _ => Tab.State.ToString().ToLowerInvariant(),
    };

    /// <summary>Only what a person would want to know: why this tab is not going to sleep on its own.</summary>
    private string ProtectionWords
    {
        get
        {
            var p = Tab.Protection;
            var reasons = new List<string>();
            if (Tab.IsPinned) reasons.Add("pinned");
            if (p.HasFlag(ProtectionFlags.KeepActive)) reasons.Add("kept active");
            if (p.HasFlag(ProtectionFlags.NeverHibernateSite)) reasons.Add("site kept active");
            if (p.HasFlag(ProtectionFlags.Audible)) reasons.Add("playing sound");
            // Named separately: "sharing your screen" and "using your microphone" are different things to be told,
            // and a person deciding whether to close a tab needs to know which one it is.
            if (p.HasFlag(ProtectionFlags.ScreenShareActive)) reasons.Add("sharing screen");
            if (p.HasFlag(ProtectionFlags.CameraActive)) reasons.Add("using camera");
            if (p.HasFlag(ProtectionFlags.MicrophoneActive)) reasons.Add("using microphone");
            if (p.HasFlag(ProtectionFlags.WebRtcActive)) reasons.Add("call active");
            if (p.HasFlag(ProtectionFlags.DownloadActive)) reasons.Add("downloading");
            if (p.HasFlag(ProtectionFlags.DirtyForm)) reasons.Add("unsaved typing");
            return string.Join(", ", reasons);
        }
    }
    public SolidColorBrush StateBrush => Brushes[Tab.State];

    /// <summary>
    /// What a screen reader announces. The colour dot carries the state visually and would otherwise be silent, so
    /// the same words go here — the state must not be available only to people who can see the colour.
    /// </summary>
    public string AccessibleName => $"{Title}. {Subtitle}.";

    /// <summary>"Close" alone tells a screen-reader user nothing about which of twenty rows they are on.</summary>
    public string CloseName => $"Close {Title}";

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        foreach (var p in new[] { nameof(Title), nameof(Subtitle), nameof(StateBrush), nameof(AccessibleName), nameof(CloseName) }) Raise(p);
    }
    private void Raise([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p));
}
