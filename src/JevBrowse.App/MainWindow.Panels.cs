using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace JevBrowse.App;

/// <summary>
/// The side panel: the place for information that a person reads and dismisses, in the place of a dialog that stops the
/// whole window. Decisions that must not be made by accident (permission prompts, destructive confirmations) stay as
/// dialogs on purpose: a panel that can be ignored is the wrong shape for them.
/// </summary>
public sealed partial class MainWindow
{
    // Room the page needs beside the panel; below this the panel takes the page's place instead of squeezing it.
    private const double PanelWidth = 360;
    private const double PanelDockMinWidth = 820;

    private string? _panelId;
    private Func<(string Title, UIElement Body)?>? _panelBuild;
    private Control? _panelReturnTo;

    private bool PanelOpen => SidePanel.Visibility == Visibility.Visible;

    /// <summary>
    /// Shows (or, if the same panel is already open, closes) a panel. <paramref name="home"/> is where focus goes back to
    /// when it closes, unless the person was somewhere else in the toolbar or sidebar when they opened it.
    /// </summary>
    private void OpenPanel(string id, Func<(string Title, UIElement Body)?> build, Control home)
    {
        if (PanelOpen && _panelId == id) { ClosePanel(); return; }
        var built = build();
        if (built is null) return;

        // Where focus was when the person asked, so closing puts them back exactly there. Only remembered while no panel is
        // open: switching panels keeps the original place.
        if (!PanelOpen)
        {
            var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as Control;
            _panelReturnTo = focused is not null && focused != AddressBox && !IsInsidePanel(focused) ? focused : home;
        }
        _panelId = id;
        _panelBuild = build;
        ShowPanelContent(built.Value);
        SidePanel.Visibility = Visibility.Visible;
        LayoutPanel();
        // Keyboard focus lands on the panel's own close button: reading order starts at the title, and Esc works at once.
        SidePanelClose.Focus(FocusState.Keyboard);
    }

    private void ShowPanelContent((string Title, UIElement Body) built)
    {
        SidePanelTitle.Text = built.Title;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SidePanel, built.Title);
        SidePanelBody.Content = built.Body;
    }

    /// <summary>Called when the active tab changes: a panel about "this page" must not go on describing the last one.</summary>
    private void RefreshPanel()
    {
        if (!PanelOpen || _panelBuild is null) return;
        var hadFocusInside = FocusManager.GetFocusedElement(Content.XamlRoot) is Control c && IsInsidePanel(c);
        var built = _panelBuild();
        if (built is null) { ClosePanel(restoreFocus: false); return; }
        ShowPanelContent(built.Value);
        // The button that was just pressed no longer exists; without this, focus would fall out of the window.
        if (hadFocusInside) SidePanelClose.Focus(FocusState.Keyboard);
    }

    private void ClosePanel(bool restoreFocus = true)
    {
        if (!PanelOpen) return;
        var hadFocusInside = FocusManager.GetFocusedElement(Content.XamlRoot) is Control c && IsInsidePanel(c);
        SidePanel.Visibility = Visibility.Collapsed;
        SidePanelBody.Content = null;
        _panelId = null; _panelBuild = null;
        LayoutPanel();
        // Focus is only moved if it was in the panel. If the person had already clicked into the page, it stays there.
        if (!restoreFocus || !hadFocusInside) { _panelReturnTo = null; return; }
        var target = _panelReturnTo is { IsLoaded: true, Visibility: Visibility.Visible, IsEnabled: true } r ? r : AddressBox;
        _panelReturnTo = null;
        target.Focus(FocusState.Keyboard);
    }

    private bool IsInsidePanel(DependencyObject d)
    {
        for (var p = d; p is not null; p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(p))
            if (ReferenceEquals(p, SidePanel)) return true;
        return false;
    }

    private void OnSidePanelClose(object s, RoutedEventArgs e) => ClosePanel();

    private void OnSidePanelKeyDown(object s, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        ClosePanel();
    }

    private void OnMainAreaSizeChanged(object s, SizeChangedEventArgs e) => LayoutPanel();

    /// <summary>Beside the page when there is room; in place of it when there is not. Never over it: a web view is drawn above other controls.</summary>
    private void LayoutPanel()
    {
        var open = PanelOpen;
        var narrow = MainArea.ActualWidth < PanelDockMinWidth;
        PageSurface.Visibility = open && narrow ? Visibility.Collapsed : Visibility.Visible;
        PageSurface.Margin = open && !narrow ? new Thickness(0, 0, PanelWidth + Tokens.Space(8), 0) : new Thickness(0);
        SidePanel.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        SidePanel.Width = narrow ? double.NaN : PanelWidth;
    }
}
