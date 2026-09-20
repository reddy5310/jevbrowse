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
    public string Subtitle => $"{Tab.State}{(Tab.Protection != ProtectionFlags.None ? " • " + Tab.Protection : "")} • {(Tab.Url.Scheme == "jev" ? "local page" : Tab.Url.Host)}";
    public SolidColorBrush StateBrush => Brushes[Tab.State];

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        foreach (var p in new[] { nameof(Title), nameof(Subtitle), nameof(StateBrush) }) Raise(p);
    }
    private void Raise([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p));
}
