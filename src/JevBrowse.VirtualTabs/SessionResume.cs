using JevBrowse.Domain;

namespace JevBrowse.VirtualTabs;

/// <summary>Where to pick up: a workspace, and the tab to bring to the front in it (null = the workspace has nothing to show yet).</summary>
public sealed record ResumeChoice(ContextId Workspace, ResourceId? Tab);

/// <summary>
/// Which workspace and tab to bring back at start-up, from the two ids remembered when the app last ran. Pure, so every fallback can be tested.
/// Only ORDINARY workspaces are ever remembered or resumed: a Private session, a Disposable workspace (an agent's) and anything else that leaves no trace
/// is never selected, whatever the saved ids say. Anything missing falls back to the default behaviour (null).
/// </summary>
public static class SessionResume
{
    public static ResumeChoice? Choose(string? workspaceId, string? tabId, IReadOnlyList<Workspace> workspaces, IReadOnlyList<VirtualTab> tabs)
    {
        bool Ordinary(ContextId id) => workspaces.FirstOrDefault(w => w.Id == id) is { } w && !w.Container.IsEphemeral();

        if (Guid.TryParseExact(tabId ?? "", "N", out var t)
            && tabs.FirstOrDefault(x => x.Id.Value == t) is { } tab && Ordinary(tab.WorkspaceId) && tab.State != ResourceState.Archived)
            return new(tab.WorkspaceId, tab.Id);

        if (Guid.TryParseExact(workspaceId ?? "", "N", out var w) && new ContextId(w) is var ws && Ordinary(ws))
            return new(ws, tabs.LastOrDefault(x => x.WorkspaceId == ws && x.State != ResourceState.Archived)?.Id);

        return null;
    }
}
