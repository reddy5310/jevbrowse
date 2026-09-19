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
        [ResourceState.Hot] = new(Colors.LimeGreen),
        [ResourceState.Warm] = new(Colors.Gold),
        [ResourceState.Cold] = new(Colors.DodgerBlue),
        [ResourceState.Suspended] = new(Colors.SlateBlue),
        [ResourceState.Virtual] = new(Colors.Gray),
        [ResourceState.Archived] = new(Colors.DimGray),
    };

    public VirtualTab Tab { get; } = tab;
    public ResourceId Id => Tab.Id;
    public string Title => string.IsNullOrWhiteSpace(Tab.Title) ? Tab.Url.Host : Tab.Title;
    public string Subtitle => $"{Tab.State}{(Tab.Protection != ProtectionFlags.None ? " • " + Tab.Protection : "")} • {Tab.Url.Host}";
    public SolidColorBrush StateBrush => Brushes[Tab.State];

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        foreach (var p in new[] { nameof(Title), nameof(Subtitle), nameof(StateBrush) }) Raise(p);
    }
    private void Raise([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p));
}
