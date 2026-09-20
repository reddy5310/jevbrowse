using Microsoft.UI.Xaml.Controls;

namespace JevBrowse.App;

public static class DialogExtensions
{
    // WinUI allows ONE ContentDialog per window at a time; a second ShowAsync throws COMException. The app opens
    // dialogs from many places, and some are opened by the WEB PAGE's behaviour (a permission request), so two can
    // easily coincide: a site asking for the camera while Settings is open, or two sites asking within seconds.
    // That exception was thrown inside an un-awaited dispatcher callback and ended the process. Every dialog now
    // waits its turn instead.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ContentDialogResult> ShowSerializedAsync(this ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try { return await dialog.ShowAsync(); }
        finally { Gate.Release(); }
    }
}
