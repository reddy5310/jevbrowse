using System.ComponentModel;
using System.Runtime.CompilerServices;
using JevBrowse.Domain;
using JevBrowse.VirtualTabs;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace JevBrowse.App;

/// <summary>Sidebar view of a VirtualTab. Presentation only; the kernel owns the truth.</summary>
public sealed class TabItem(VirtualTab tab) : INotifyPropertyChanged
{

    public VirtualTab Tab { get; } = tab;
    public ResourceId Id => Tab.Id;
    public string Title => Tab.Url.Scheme == "jev" ? "Welcome to JevBrowse" : string.IsNullOrWhiteSpace(Tab.Title) ? Tab.Url.Host : Tab.Title;
    public string Subtitle => string.Join(" • ", new[] { StateWord, ProtectionWords, Tab.Url.Scheme == "jev" ? "local page" : Tab.Url.Host }.Where(s => s.Length > 0));

    /// <summary>What the tab is doing, in the words a person would use. Internal state names are for diagnostics.</summary>
    private string StateWord => TabStateWords.Of(Tab.State);

    /// <summary>Why this tab is special, in words: placement first, then the reasons it will not sleep on its own.</summary>
    private string ProtectionWords =>
        string.Join(", ", (Tab.IsPinned ? new[] { "pinned" } : []).Concat(ProtectionPhrases.StayAwake(Tab.Protection)));
    /// <summary>
    /// The dot is redundant with the word beside it ("open", "sleeping"), never the only carrier. Its colour is a token,
    /// asked for again on every refresh so a theme switch reaches it.
    /// </summary>
    public Brush StateBrush => Tokens.Brush(Tab.State switch
    {
        ResourceState.Hot => "JevStateHotBrush",
        ResourceState.Warm => "JevStateWarmBrush",
        ResourceState.Cold => "JevStateColdBrush",
        ResourceState.Suspended => "JevStateSuspendedBrush",
        ResourceState.Archived => "JevStateArchivedBrush",
        _ => "JevStateVirtualBrush",
    });

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
