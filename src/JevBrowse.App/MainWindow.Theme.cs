using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace JevBrowse.App;

public sealed partial class MainWindow
{
    private ThemePreference _themePref = ThemePreference.Dark;
    private readonly UISettings _uiSettings = new();

    /// <summary>Dark is the default; Light and "match Windows" are choices. JEVBROWSE_THEME overrides for tests.</summary>
    private void InitTheme()
    {
        var chosen = UiPrefs.Load(DataDir).Theme;
        if (Enum.TryParse<ThemePreference>(Environment.GetEnvironmentVariable("JEVBROWSE_THEME"), true, out var forced)) chosen = forced;
        ApplyTheme(chosen, remember: false);

        // Follow Windows. Windows raises ColorValuesChanged for a light/dark switch AND for a contrast-theme switch, so
        // this one subscription covers both. (AccessibilitySettings.HighContrastChanged is not available to an
        // unpackaged app: subscribing throws in the constructor and the app never opens. The crash recorder named it.)
        // Following Windows is a convenience, so if the subscription is refused the app still starts, on the theme it was given.
        try { _uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(() => ApplyTheme(_themePref, remember: false)); }
        catch (Exception) { }
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
