using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace JevBrowse.App;

public enum ThemePreference { Dark, Light, System }

/// <summary>
/// Small, non-secret interface preferences. One file, read and written whole, so saving one setting can never erase
/// another (the earlier settings file was overwritten by first-run and lost everything else in it).
/// </summary>
public sealed record UiPrefs(bool SidebarCollapsed = false, ThemePreference Theme = ThemePreference.Dark)
{
    private static string PathFor(string dataDir) => System.IO.Path.Combine(dataDir, "ui-prefs.json");

    public static UiPrefs Load(string dataDir)
    {
        try
        {
            var p = PathFor(dataDir);
            if (!File.Exists(p)) return new UiPrefs();
            using var d = JsonDocument.Parse(File.ReadAllText(p));
            var r = d.RootElement;
            var theme = r.TryGetProperty("theme", out var t) && t.ValueKind == JsonValueKind.String
                        && Enum.TryParse<ThemePreference>(t.GetString(), true, out var parsed) ? parsed : ThemePreference.Dark;
            return new UiPrefs(r.TryGetProperty("sidebarCollapsed", out var s) && s.ValueKind == JsonValueKind.True, theme);
        }
        catch (Exception) { return new UiPrefs(); }
    }

    public void Save(string dataDir)
    {
        try { File.WriteAllText(PathFor(dataDir), JsonSerializer.Serialize(new { sidebarCollapsed = SidebarCollapsed, theme = Theme.ToString().ToLowerInvariant() })); }
        catch (Exception) { /* a preference, not data */ }
    }
}

/// <summary>
/// Looks up the colours the code (not the markup) has to choose: tab-state dots and the class badge. They come from the
/// same token dictionaries as everything else, for the theme that is showing, so nothing in code names a raw colour.
/// </summary>
public static class Tokens
{
    /// <summary>"Dark", "Light" or "HighContrast": the ThemeDictionaries key in App.xaml that is in force.</summary>
    public static string Key { get; private set; } = "Dark";
    public static bool HighContrast { get; private set; }

    public static void Set(ElementTheme effective, bool highContrast)
    {
        HighContrast = highContrast;
        Key = highContrast ? "HighContrast" : effective == ElementTheme.Light ? "Light" : "Dark";
    }

    /// <summary>The brush for a token in the current theme. Never throws: a missing token falls back to the primary text colour.</summary>
    public static Brush Brush(string name)
    {
        try
        {
            var dicts = Application.Current.Resources.ThemeDictionaries;
            if (dicts.TryGetValue(Key, out var d) && d is ResourceDictionary dict && dict.TryGetValue(name, out var b) && b is Brush brush && !HighContrast)
                return brush;
        }
        catch (Exception) { /* fall through */ }

        // High contrast, or anything unresolvable: use what Windows says the foreground and background are, never a hue
        // of ours. (State and data class are also written as words, so colour is never the only carrier.)
        var ui = new Windows.UI.ViewManagement.UISettings();
        var wantsBackground = name.Contains("Badge", StringComparison.Ordinal) && !name.Contains("Text", StringComparison.Ordinal);
        var c = ui.GetColorValue(wantsBackground ? Windows.UI.ViewManagement.UIColorType.Background : Windows.UI.ViewManagement.UIColorType.Foreground);
        return new SolidColorBrush(c);
    }
}
