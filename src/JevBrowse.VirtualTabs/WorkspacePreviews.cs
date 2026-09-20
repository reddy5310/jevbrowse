using JevBrowse.Domain;

namespace JevBrowse.VirtualTabs;

/// <summary>The words a person uses for a tab's state. Internal state names are for diagnostics only.</summary>
public static class TabStateWords
{
    public static string Of(ResourceState s) => s switch
    {
        ResourceState.Hot => "open",
        ResourceState.Warm => "open, in the background",
        ResourceState.Cold => "idle",
        ResourceState.Suspended => "dozing",
        ResourceState.Virtual => "sleeping",
        ResourceState.Archived => "archived",
        _ => s.ToString().ToLowerInvariant(),
    };
}

public sealed record TabPreview(ResourceId Id, string Title, string Host, string State, string? ThumbnailPath, bool IsActive);

public sealed record WorkspacePreview(
    ContextId Id, string Name, IdentityContainer Container, bool IsActive, int TabCount, int AwakeCount,
    string Summary, IReadOnlyList<TabPreview> Tabs, int HiddenTabs);

/// <summary>
/// What each workspace holds, so a person can choose where to go without switching first. Built from the same facts the sidebar
/// uses; it decides only what may be SHOWN: a private or disposable session is left out of the overview unless it is the one
/// you are in, and nothing from one ever carries a saved image.
/// </summary>
public static class WorkspacePreviews
{
    public static IReadOnlyList<WorkspacePreview> Build(
        IEnumerable<Workspace> workspaces, IEnumerable<VirtualTab> tabs, ContextId active, ResourceId? activeTab,
        Func<VirtualTab, string> titleOf, Func<VirtualTab, string?> thumbnailOf, int maxTabs = 6)
    {
        var all = tabs.ToList();
        var cards = new List<WorkspacePreview>();
        foreach (var w in workspaces)
        {
            var isActive = w.Id == active;
            var ephemeral = w.Container.IsEphemeral();
            if (ephemeral && !isActive) continue;
            var mine = all.Where(t => t.WorkspaceId == w.Id).ToList();
            var awake = mine.Count(t => t.State.HasLiveRenderer());
            var previews = mine.Take(maxTabs).Select(t => new TabPreview(
                t.Id, titleOf(t), t.Url.Scheme == "jev" ? "local page" : t.Url.Host, TabStateWords.Of(t.State),
                ephemeral ? null : thumbnailOf(t), isActive && activeTab is { } a && a == t.Id)).ToList();
            var count = mine.Count == 1 ? "1 tab" : $"{mine.Count} tabs";
            var summary = mine.Count == 0 ? $"No tabs · {w.Container}"
                : $"{count}, {awake} awake · {w.Container}{(ephemeral ? " · nothing is kept after it ends" : "")}";
            cards.Add(new WorkspacePreview(w.Id, w.Name, w.Container, isActive, mine.Count, awake, summary, previews, Math.Max(0, mine.Count - maxTabs)));
        }
        return cards.OrderByDescending(c => c.IsActive).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
