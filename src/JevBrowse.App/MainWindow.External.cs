using JevBrowse.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JevBrowse.App;

/// <summary>Links handed to the browser by other programs, and the registration that lets Windows offer JevBrowse as a default browser.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Opens a link from another program in a NEW tab of the default (ordinary) workspace, and brings the window forward. It never goes into a Private session or an
    /// agent's workspace: where a link came from is not something those sessions should learn or receive.
    /// </summary>
    internal async Task OpenExternalLinkAsync(Uri url)
    {
        try
        {
            if (_kernel is null || !_ready) { _pendingExternalLink = url; return; }
            await _kernel.SwitchWorkspaceAsync(ContextId.Default);
            var t = _kernel.Open(url);
            await _kernel.ActivateAsync(t.Id);
            RebuildWorkspaces();
            Activate();
        }
        catch (Exception ex) { StatusText.Text = "Could not open the link: " + ex.Message; }
    }

    private Uri? _pendingExternalLink;

    private async void OnMakeDefault(object s, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath ?? "";
            if (!File.Exists(exe)) { StatusText.Text = "Could not find the JevBrowse program file to register."; return; }
            DefaultBrowser.Register(exe);
            var dlg = new ContentDialog
            {
                Title = "Choose JevBrowse in Windows settings",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "JevBrowse is now listed as a browser. Windows does not let a program make itself the default, so you choose it: in the Settings page that opens, "
                         + "pick JevBrowse under \"Web browser\" (or under http and https). Links from other programs will then open here.\n\n"
                         + "Nothing changes until you choose. To undo it later, pick another browser in the same place; \"Remove JevBrowse from the browser list\" in the More menu removes the listing.",
                },
                PrimaryButtonText = "Open Settings", CloseButtonText = "Not now", DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot,
            };
            if (await dlg.ShowSerializedAsync() == ContentDialogResult.Primary) await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
        }
        catch (Exception ex) { StatusText.Text = "Could not register JevBrowse as a browser: " + ex.Message; }
    }

    private void OnRemoveDefault(object s, RoutedEventArgs e)
    {
        try { DefaultBrowser.Unregister(); StatusText.Text = "JevBrowse was removed from the browser list. If it was your default, choose another browser in Windows Settings > Default apps."; }
        catch (Exception ex) { StatusText.Text = "Could not remove the listing: " + ex.Message; }
    }
}
