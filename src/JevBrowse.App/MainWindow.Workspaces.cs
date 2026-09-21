using JevBrowse.Domain;
using JevBrowse.VirtualTabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace JevBrowse.App;

/// <summary>
/// Workspace session previews (P3): what each workspace holds, in a panel, before you switch. The facts and the privacy rules
/// (private sessions left out, no saved image from one) are in <see cref="WorkspacePreviews"/>, where tests hold them.
/// </summary>
public sealed partial class MainWindow
{
    private void OnWorkspaceOverview(object s, RoutedEventArgs e) => OpenPanel("workspaces", BuildWorkspaceOverview, MoreButton, live: false);

    private async Task RenameWorkspaceAsync(ContextId id)
    {
        try
        {
            var ws = _kernel!.Workspaces.First(w => w.Id == id);
            var box = new TextBox { Text = ws.Name, SelectionStart = 0, SelectionLength = ws.Name.Length, MaxLength = 60, AcceptsReturn = false };
            AutomationProperties.SetName(box, "Workspace name");
            var dlg = new ContentDialog { Title = "Rename workspace", Content = box, PrimaryButtonText = "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot };
            if (await dlg.ShowSerializedAsync() != ContentDialogResult.Primary) return;
            if (string.IsNullOrWhiteSpace(box.Text)) { StatusText.Text = "A workspace needs a name."; return; }
            _kernel.RenameWorkspace(id, box.Text);
            StatusText.Text = $"Renamed to {box.Text.Trim()}.";
        }
        catch (Exception ex) { StatusText.Text = "Could not rename the workspace: " + ex.Message; }
    }

    private async Task DeleteWorkspaceWithConfirmAsync(ContextId id)
    {
        try
        {
            var k = _kernel!;
            var ws = k.Workspaces.First(w => w.Id == id);
            var count = k.TabsIn(id).Count();
            var dlg = new ContentDialog
            {
                Title = $"Delete the workspace \"{ws.Name}\"?",
                Content = new TextBlock
                {
                    Text = $"This closes its {count} tab{(count == 1 ? "" : "s")} and forgets their saved places and Back/Forward history. It does not sign you out of any site and does not delete downloaded files or Browser Memory; "
                         + "those have their own actions.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Delete workspace", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot,
            };
            if (await dlg.ShowSerializedAsync() != ContentDialogResult.Primary) return;
            var back = k.ActiveWorkspace != id ? k.ActiveWorkspace : ContextId.Default;
            await k.DeleteWorkspaceAsync(id, back);
            if (k.Active is null && k.TabsIn(k.ActiveWorkspace).LastOrDefault() is { } next) await k.ActivateAsync(next.Id);
            RebuildWorkspaces(); RebuildList();
            StatusText.Text = $"Deleted the workspace {ws.Name}.";
        }
        catch (Exception ex) { StatusText.Text = "Could not delete the workspace: " + ex.Message; }
    }

    private (string Title, UIElement Body)? BuildWorkspaceOverview()
    {
        if (_kernel is null) return null;
        var k = _kernel;
        var cards = WorkspacePreviews.Build(k.Workspaces, k.Tabs, k.ActiveWorkspace, k.Active?.Id,
            t => new TabItem(t).Title,
            t => k.GetCheckpoint(t.Id)?.ThumbnailPath is { } p && File.Exists(p) ? p : null);

        var secondary = Tokens.Brush("JevTextSecondaryBrush");
        var body = new StackPanel { Spacing = Tokens.Space(12) };
        foreach (var card in cards)
        {
            var c = card;
            var inner = new StackPanel { Spacing = Tokens.Space(6) };
            inner.Children.Add(new TextBlock { Text = c.IsActive ? $"{c.Name} (you are here)" : c.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
            inner.Children.Add(new TextBlock { Text = c.Summary, FontSize = 12, Foreground = secondary, TextWrapping = TextWrapping.Wrap });

            // Saved images are labelled for what they are: a picture of an earlier moment, never the live page.
            var withImage = c.Tabs.Where(t => t.ThumbnailPath is not null).Take(3).ToList();
            if (withImage.Count > 0)
            {
                var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space(6) };
                foreach (var t in withImage)
                {
                    try
                    {
                        var bmp = new BitmapImage { DecodePixelWidth = 160 };
                        using (var fs = File.OpenRead(t.ThumbnailPath!)) bmp.SetSource(fs.AsRandomAccessStream());
                        var img = new Image { Source = bmp, Width = 96, Height = 60, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
                        AutomationProperties.SetName(img, $"Saved image of {t.Title}, from earlier: not the live page");
                        strip.Children.Add(new Border { CornerRadius = (Microsoft.UI.Xaml.CornerRadius)Application.Current.Resources["JevRadiusSm"], Child = img });
                    }
                    catch (Exception) { /* a missing or unreadable image is just not shown */ }
                }
                if (strip.Children.Count > 0)
                {
                    inner.Children.Add(strip);
                    inner.Children.Add(new TextBlock { Text = "Saved images from earlier, not the live pages", FontSize = 11, Foreground = secondary });
                }
            }

            foreach (var t in c.Tabs)
            {
                var tp = t;
                var row = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Tokens.Brush("JevSurface2Brush"),
                    BorderThickness = new Thickness(0), Padding = Tokens.Inset("JevInsetBox"),
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = tp.IsActive ? tp.Title + " (current)" : tp.Title, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock { Text = $"{tp.Host} • {tp.State}", FontSize = 12, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis },
                        },
                    },
                };
                AutomationProperties.SetName(row, $"Go to {tp.Title}. {tp.Host}, {tp.State}.");
                row.Click += async (_, _) =>
                {
                    ClosePanel(restoreFocus: false);
                    if (k.ActiveWorkspace != c.Id) { await k.SwitchWorkspaceAsync(c.Id); RebuildWorkspaces(); }
                    await k.ActivateAsync(tp.Id);
                };
                inner.Children.Add(row);
            }
            if (c.HiddenTabs > 0) inner.Children.Add(new TextBlock { Text = $"and {c.HiddenTabs} more", FontSize = 12, Foreground = secondary });

            if (!c.IsActive)
            {
                var go = new Button { Content = $"Switch to {c.Name}", HorizontalAlignment = HorizontalAlignment.Stretch };
                go.Click += async (_, _) => { ClosePanel(restoreFocus: false); await k.SwitchWorkspaceAsync(c.Id); RebuildWorkspaces(); };
                inner.Children.Add(go);
            }

            // Rename and delete are for ordinary workspaces only (a Private session or an agent's workspace is temporary and ended, not managed).
            var real = k.Workspaces.FirstOrDefault(w => w.Id == c.Id);
            if (real is not null && !real.Container.IsEphemeral())
            {
                var manage = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space(8) };
                var rename = new Button { Content = "Rename…", Style = (Style)Application.Current.Resources["JevToolButton"] };
                AutomationProperties.SetName(rename, $"Rename workspace {c.Name}");
                rename.Click += async (_, _) => { ClosePanel(restoreFocus: false); await RenameWorkspaceAsync(c.Id); OnWorkspaceOverview(this, new RoutedEventArgs()); };
                manage.Children.Add(rename);
                if (c.Id != ContextId.Default)
                {
                    var delete = new Button { Content = "Delete…", Style = (Style)Application.Current.Resources["JevToolButton"] };
                    AutomationProperties.SetName(delete, $"Delete workspace {c.Name}");
                    delete.Click += async (_, _) => { ClosePanel(restoreFocus: false); await DeleteWorkspaceWithConfirmAsync(c.Id); OnWorkspaceOverview(this, new RoutedEventArgs()); };
                    manage.Children.Add(delete);
                }
                inner.Children.Add(manage);
            }

            body.Children.Add(new Border
            {
                Padding = Tokens.Inset("JevInsetBox"), CornerRadius = (Microsoft.UI.Xaml.CornerRadius)Application.Current.Resources["JevRadiusCard"],
                Background = Tokens.Brush("JevSurface2Brush"), BorderBrush = Tokens.Brush(c.IsActive ? "JevAccentSolidBrush" : "JevBorderSubtleBrush"), BorderThickness = new Thickness(1),
                Child = inner,
            });
        }
        return ("Workspaces", body);
    }
}
