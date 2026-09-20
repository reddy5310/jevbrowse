using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace JevBrowse.App;

public sealed partial class MainWindow
{
    private ThemePreference _themePref = ThemePreference.Dark;
    private readonly UISettings _uiSettings = new();

    /// <summary>Dark is the default; Light and "match Windows" are choices. JEVBROWSE_THEME overrides for tests.</summary>
    private readonly List<(Microsoft.UI.Xaml.Controls.Button Button, double BaseSize)> _glyphButtons = [];

    /// <summary>Finds the icon-only buttons (a symbol font, no words) so their size can be managed rather than left to grow without limit.</summary>
    private void CollectGlyphButtons(DependencyObject node)
    {
        switch (node)
        {
            case Microsoft.UI.Xaml.Controls.Button b when b.FontFamily?.Source?.Contains("MDL2", StringComparison.OrdinalIgnoreCase) == true:
                b.IsTextScaleFactorEnabled = false; _glyphButtons.Add((b, b.FontSize)); return;
            case Microsoft.UI.Xaml.Controls.Panel p: foreach (var c in p.Children) CollectGlyphButtons(c); break;
            case Microsoft.UI.Xaml.Controls.Border br when br.Child is not null: CollectGlyphButtons(br.Child); break;
            case Microsoft.UI.Xaml.Controls.ScrollViewer sv when sv.Content is DependencyObject sc: CollectGlyphButtons(sc); break;
            case Microsoft.UI.Xaml.Controls.ContentControl cc when cc.Content is DependencyObject cco: CollectGlyphButtons(cco); break;
        }
    }

    /// <summary>
    /// Windows "Text size" is for reading. Icons follow it, but only up to 150%: past that they would take the room the words
    /// need, while at 100% they must not be left tiny beside enlarged text (they are click and touch targets). The words scale fully.
    /// The sidebar's memory readout is secondary, so at large sizes it is held to two lines (the full text is in its tooltip)
    /// rather than pushing the tab list, which is not secondary, out of the sidebar.
    /// </summary>
    private void ApplyTextScale()
    {
        double scale;
        try { scale = _uiSettings.TextScaleFactor; } catch (Exception) { scale = 1; }
        foreach (var (button, baseSize) in _glyphButtons) button.FontSize = baseSize * Math.Min(scale, 1.5);
        var large = scale >= 1.4;
        PoolText.MaxLines = large ? 2 : 0;
        PoolText.TextTrimming = large ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        ToolTipService.SetToolTip(PoolText, large ? PoolText.Text : null);
        LayoutToolbar();
    }

    private void InitTheme()
    {
        CollectGlyphButtons(Root);
        var chosen = UiPrefs.Load(DataDir).Theme;
        if (Enum.TryParse<ThemePreference>(Environment.GetEnvironmentVariable("JEVBROWSE_THEME"), true, out var forced)) chosen = forced;
        ApplyTheme(chosen, remember: false);

        // Light identifies the active surface: the panel, the address bar or the page you last worked in carries the
        // accent edge, the others do not. It is a border colour, set when focus moves. There is no animation to run at rest.
        Sidebar.GotFocus += (_, _) => SetActiveSurface(ActiveSurface.Sidebar);
        AddressPill.GotFocus += (_, _) => SetActiveSurface(ActiveSurface.Address);
        PageSurface.GotFocus += (_, _) => SetActiveSurface(ActiveSurface.Page);

        // Follow Windows. Windows raises ColorValuesChanged for a light/dark switch AND for a contrast-theme switch, so
        // this one subscription covers both. (AccessibilitySettings.HighContrastChanged is not available to an
        // unpackaged app: subscribing throws in the constructor and the app never opens. The crash recorder named it.)
        // Following Windows is a convenience, so if the subscription is refused the app still starts, on the theme it was given.
        try { _uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(() => ApplyTheme(_themePref, remember: false)); }
        catch (Exception) { }
        // A change of Windows "Text size" while the app is open changes every button's width: lay the toolbar out again.
        try { _uiSettings.TextScaleFactorChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyTextScale); } catch (Exception) { /* cannot tell: the size-changed path still covers the window */ }
        ApplyTextScale();
    }

    /// <summary>
    /// What pages are told the colour scheme is. An explicit Dark or Light in the app is what pages see (so the welcome
    /// page and any site that supports prefers-color-scheme match the window); "match Windows" leaves it to Windows.
    /// </summary>
    private void ApplyPageScheme(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        try
        {
            core.Profile.PreferredColorScheme = _themePref switch
            {
                ThemePreference.Dark => Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Dark,
                ThemePreference.Light => Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Light,
                _ => Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Auto,
            };
        }
        catch (Exception) { /* a page that keeps Windows' scheme is a cosmetic mismatch, never a failure */ }
    }

    private enum ActiveSurface { None, Sidebar, Address, Page }
    private ActiveSurface _activeSurface = ActiveSurface.None;

    private void SetActiveSurface(ActiveSurface s) { _activeSurface = s; UpdateActiveSurface(); }

    private void UpdateActiveSurface()
    {
        var on = Tokens.Brush("JevAccentSolidBrush");
        var off = Tokens.Brush("JevBorderSubtleBrush");
        Sidebar.BorderBrush = _activeSurface == ActiveSurface.Sidebar ? on : off;
        AddressPill.BorderBrush = _activeSurface == ActiveSurface.Address ? on : off;
        PageSurface.BorderBrush = _activeSurface == ActiveSurface.Page ? on : off;
    }

    private bool SystemPrefersLight()
    {
        try
        {
            var bg = _uiSettings.GetColorValue(UIColorType.Background);
            return 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B > 127;
        }
        catch (Exception) { return false; }   // cannot tell: stay dark, the default
    }

    private static bool IsHighContrast()
    {
        try { return new AccessibilitySettings().HighContrast; }
        catch (Exception) { return false; }
    }

    private void ApplyTheme(ThemePreference pref, bool remember)
    {
        _themePref = pref;
        var effective = pref switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.System => SystemPrefersLight() ? ElementTheme.Light : ElementTheme.Dark,
            _ => ElementTheme.Dark,
        };
        Root.RequestedTheme = effective;
        Tokens.Set(effective, IsHighContrast());

        // Everything the markup names follows by itself (ThemeResource). What code chooses has to be asked again.
        foreach (var i in Items) i.Refresh();
        UpdateClassBadge();

        UpdateActiveSurface();

        // Pages follow the app's theme too, not only Windows': the browser tells them which scheme to use.
        if (_leases is not null)
            foreach (var id in _leases.LiveResources.ToList())
                if (_leases.TryGet(id, out var l) && l is JevBrowse.App.Renderer.WebView2Lease { View.CoreWebView2: { } core }) ApplyPageScheme(core);

        if (remember) UiPrefs.Load(DataDir).With(pref).Save(DataDir);
        if (remember)
            StatusText.Text = pref switch
            {
                ThemePreference.Dark => "Dark theme.",
                ThemePreference.Light => "Light theme.",
                _ => "Following Windows: " + (effective == ElementTheme.Light ? "light" : "dark") + " right now.",
            };
    }
}

internal static class UiPrefsExtensions
{
    public static UiPrefs With(this UiPrefs p, ThemePreference theme) => p with { Theme = theme };
    public static UiPrefs With(this UiPrefs p, bool sidebarCollapsed) => p with { SidebarCollapsed = sidebarCollapsed };
}
