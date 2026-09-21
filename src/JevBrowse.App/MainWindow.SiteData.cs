using JevBrowse.App.Renderer;
using JevBrowse.Domain;
using JevBrowse.TrustOS;
using JevBrowse.VirtualTabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace JevBrowse.App;

/// <summary>
/// The private-alpha essentials that are about the person's own data: what a site was allowed to do, clearing website data, picking up where they left off,
/// saving a file out of a Private session, and a WebView2 runtime that is missing.
/// </summary>
public sealed partial class MainWindow
{
    // ---------------------------------------------------------------- resume the previous session

    private ResumeChoice? ResumeChoiceFromPrefs()
    {
        var prefs = UiPrefs.Load(DataDir);
        return SessionResume.Choose(prefs.LastWorkspace, prefs.LastTab, _kernel!.Workspaces, _kernel.Tabs);
    }

    /// <summary>
    /// Remembers the tab in front, but only if it belongs to an ORDINARY workspace. Private and Disposable (agent) tabs are never written down, and coming back
    /// to the browser after one leaves the last ordinary choice as it was. Only two ids are stored, never an address.
    /// </summary>
    private void RememberResumePoint()
    {
        try
        {
            if (_kernel?.Active is not { } t || _kernel.ContainerOf(t).IsEphemeral()) return;
            var prefs = UiPrefs.Load(DataDir);
            if (prefs.LastTab == t.Id.ToString() && prefs.LastWorkspace == t.WorkspaceId.ToString()) return;
            (prefs with { LastWorkspace = t.WorkspaceId.ToString(), LastTab = t.Id.ToString() }).Save(DataDir);
        }
        catch (Exception) { /* a convenience, never a reason to fail */ }
    }

    // ---------------------------------------------------------------- WebView2 runtime

    private static bool WebView2RuntimeAvailable(out string problem)
    {
        try
        {
            var v = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(v)) { problem = "no runtime was found"; return false; }
            problem = ""; return true;
        }
        catch (Exception ex) { problem = ex.Message; return false; }
    }

    // ---------------------------------------------------------------- downloads

    /// <summary>Set only by the measurement check, which has nobody to answer the question.</summary>
    internal bool? DownloadAnswerForCheck { get; set; }

    /// <summary>
    /// Asked before a page saves a file. Ordinary tabs: yes, as before. An agent's page: never. A Private or Disposable tab: the person is told that the file
    /// outlives the session and can cancel; if they continue, the engine's normal save flow runs.
    /// </summary>
    private async Task<bool> ConfirmDownloadAsync(ResourceId id, string fileName, Uri? source, bool agentPage)
    {
        if (agentPage) return false;
        var tab = _kernel?.Tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null || !_kernel!.ContainerOf(tab).IsEphemeral()) return true;
        if (DownloadAnswerForCheck is { } canned) return canned;
        var tcs = new TaskCompletionSource<bool>();
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var host = source?.Host is { Length: > 0 } h ? h : tab.Url.Host;
                var dlg = new ContentDialog
                {
                    Title = "Save a file from a Private session?",
                    Content = new TextBlock
                    {
                        Text = $"{host} wants to save {fileName}.\n\nThis file stays on your computer after the Private session ends. Ending the session deletes the session's cookies and website data, not files you save. "
                             + "Choose Cancel if you do not want a copy left behind.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = "Continue to save…", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot,
                };
                tcs.TrySetResult(await dlg.ShowSerializedAsync() == ContentDialogResult.Primary);
            }
            catch (Exception) { tcs.TrySetResult(false); }
        })) return false;
        return await tcs.Task;
    }

    // ---------------------------------------------------------------- site permissions

    private static string Describe(PermissionGrant g, DateTimeOffset now)
    {
        var what = PermissionKinds.Phrase(g.Kind);
        var verdict = g.Allowed ? "Allowed" : "Blocked";
        var span = g.ExpiresAt is { } e ? $" until {e.ToLocalTime():t}" : " (until you reset it)";
        return $"{char.ToUpperInvariant(what[0])}{what[1..]}: {verdict}{span}";
    }

    private async void OnSitePermissions(object s, RoutedEventArgs e)
    {
        try
        {
            if (_kernel?.Active is not { } tab || _permissions is null) return;
            if (tab.Url.Scheme is not ("http" or "https")) { StatusText.Text = "Site permissions apply to web pages."; return; }
            var container = _kernel.ContainerOf(tab); var isolation = tab.WorkspaceId; var origin = tab.Url;
            var where = $"{origin.Scheme}://{origin.Host}{(origin.IsDefaultPort ? "" : ":" + origin.Port)}";

            var list = new StackPanel { Spacing = Tokens.Space(8) };
            var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
            void Rebuild()
            {
                list.Children.Clear();
                var grants = _permissions.Saved(container, isolation, origin);
                if (grants.Count == 0)
                {
                    list.Children.Add(new TextBlock { Text = "Nothing is remembered for this site in this profile. \"Just this time\" answers are never saved; the site asks each time.", TextWrapping = TextWrapping.Wrap });
                    return;
                }
                foreach (var g in grants)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space(8) };
                    row.Children.Add(new TextBlock { Text = Describe(g, DateTimeOffset.UtcNow), VerticalAlignment = VerticalAlignment.Center, MinWidth = 320 });
                    var reset = new Button { Content = "Reset", Style = (Style)Application.Current.Resources["JevToolButton"] };
                    AutomationProperties.SetName(reset, "Reset: " + PermissionKinds.Phrase(g.Kind));
                    var kind = g.Kind;
                    reset.Click += (_, _) =>
                    {
                        _permissions.Reset(container, isolation, origin, kind);
                        note.Text = "Reset. The next request from this site will be decided as if it were the first.";
                        Rebuild();
                    };
                    row.Children.Add(reset);
                    list.Children.Add(row);
                }
                var all = new Button { Content = "Reset all for this site", Style = (Style)Application.Current.Resources["JevToolButton"] };
                all.Click += (_, _) => { _permissions.Reset(container, isolation, origin); note.Text = "All saved decisions for this site were reset."; Rebuild(); };
                list.Children.Add(all);
            }
            Rebuild();

            await new ContentDialog
            {
                Title = $"Site permissions: {where}",
                Content = new ScrollViewer
                {
                    MaxHeight = 460,
                    Content = new StackPanel
                    {
                        Spacing = Tokens.Space(12),
                        Children =
                        {
                            new TextBlock { Text = $"Profile: {container}. These decisions apply to this address in this profile only, not to other sites or other profiles.", TextWrapping = TextWrapping.Wrap },
                            list,
                            note,
                            new TextBlock
                            {
                                Text = "Resetting forgets a saved answer so the next request is asked again. It does not stop a camera or microphone the page is already using: "
                                     + "that stops when the page stops it or you close the tab.",
                                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75,
                            },
                        },
                    },
                },
                CloseButtonText = "Done", XamlRoot = Content.XamlRoot,
            }.ShowSerializedAsync();
        }
        catch (Exception ex) { StatusText.Text = "Could not show site permissions: " + ex.Message; }
    }

    // ---------------------------------------------------------------- clear website data

    private async void OnClearSiteData(object s, RoutedEventArgs e)
    {
        try
        {
            if (_kernel?.Active is not { } tab) return;
            var container = _kernel.ContainerOf(tab);
            if (container.IsEphemeral())
            {
                await new ContentDialog
                {
                    Title = "Clear website data",
                    Content = new TextBlock { Text = "This is a temporary session. Its cookies and website data are deleted when the session ends; there is nothing saved to clear.", TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "OK", XamlRoot = Content.XamlRoot,
                }.ShowSerializedAsync();
                return;
            }
            if (!_leases!.TryGet(tab.Id, out var lease)) { StatusText.Text = "Open the page first, then clear its profile's website data."; return; }
            var others = string.Join(", ", Enum.GetValues<IdentityContainer>().Where(c => !c.IsEphemeral() && c != container));
            var dlg = new ContentDialog
            {
                Title = $"Clear website data for the {container} profile?",
                Content = new StackPanel
                {
                    Spacing = Tokens.Space(8),
                    Children =
                    {
                        new TextBlock { Text = $"JevBrowse cannot delete the data of a single site. This clears cookies and website storage (local storage, IndexedDB, caches, service workers) for EVERY site you have used in the {container} profile.", TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = "You will be signed out of sites in this profile.", TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock { Text = $"Not affected: the other profiles ({others}), files you downloaded, your open tabs and saved positions, and Browser Memory (saved pages you can search). Clear Browser Memory separately in Tools ▸ Search pages you have read.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 },
                    },
                },
                PrimaryButtonText = $"Clear {container} profile", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot,
            };
            if (await dlg.ShowSerializedAsync() != ContentDialogResult.Primary) return;
            var core = ((WebView2Lease)lease).View.CoreWebView2;
            await ClearProfileWebsiteDataAsync(core);
            core.Reload();
            StatusText.Text = $"Cleared cookies and website storage for the {container} profile. Other open tabs of this profile may need a reload; you are signed out of its sites.";
        }
        catch (Exception ex) { StatusText.Text = "Could not clear website data: " + ex.Message; }
    }

    /// <summary>Cookies and website storage for the profile behind this control, nothing else (no history, no downloads list, no other profile).</summary>
    internal static Task ClearProfileWebsiteDataAsync(CoreWebView2 core) =>
        core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.AllDomStorage).AsTask();
}
