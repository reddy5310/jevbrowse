using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace JevBrowse.Kernel.Tests;

/// <summary>
/// The visual system is checked against the file that defines it. Contrast is computed from the colours in App.xaml, so
/// there is no second copy to drift, and a change that breaks legibility fails here rather than in someone's eyes.
/// WCAG 2.x ratios: 4.5 for normal text, 3 for large text and for UI boundaries, focus and meaningful graphics.
/// </summary>
public class DesignTokenTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Src([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "src", "JevBrowse.App"));

    private static XElement Theme(string key)
    {
        var doc = XDocument.Load(Path.Combine(Src(), "App.xaml"));
        return doc.Descendants(P + "ResourceDictionary").Single(e => (string?)e.Attribute(X + "Key") == key
            && e.Parent?.Name.LocalName == "ResourceDictionary.ThemeDictionaries");
    }

    private static Dictionary<string, string> Colors(string theme) =>
        Theme(theme).Elements(P + "Color").ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value.Trim());

    private static HashSet<string> BrushKeys(string theme) =>
        Theme(theme).Elements().Where(e => e.Name.LocalName is "SolidColorBrush" or "LinearGradientBrush").Select(e => (string)e.Attribute(X + "Key")!).ToHashSet();

    private static (double r, double g, double b, double a) Rgba(string hex)
    {
        hex = hex.TrimStart('#');
        int a = 255; if (hex.Length == 8) { a = Convert.ToInt32(hex[..2], 16); hex = hex[2..]; }
        return (Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16), a / 255.0);
    }

    private static double Lum(string hex)
    {
        static double L(double c) { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        var (r, g, b, _) = Rgba(hex);
        return 0.2126 * L(r) + 0.7152 * L(g) + 0.0722 * L(b);
    }

    private static double Ratio(string a, string b)
    {
        var (hi, lo) = (Math.Max(Lum(a), Lum(b)), Math.Min(Lum(a), Lum(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>The colour a translucent overlay produces over a given backdrop.</summary>
    private static string Over(string overlay, string backdrop)
    {
        var (r, g, b, a) = Rgba(overlay); var (br, bg, bb, _) = Rgba(backdrop);
        int C(double f, double back) => (int)Math.Round(f * a + back * (1 - a));
        return $"#{C(r, br):X2}{C(g, bg):X2}{C(b, bb):X2}";
    }

    public static IEnumerable<object[]> Themes => [["Dark"], ["Light"]];

    [Theory, MemberData(nameof(Themes))]
    public void Text_is_legible_on_every_surface_it_can_sit_on(string theme)
    {
        var c = Colors(theme);
        foreach (var s in new[] { "JevSurface0", "JevSurface1", "JevSurface2", "JevBackdrop" })
        {
            Assert.True(Ratio(c["JevTextPrimary"], c[s]) >= 7.0, $"{theme}: primary text on {s} = {Ratio(c["JevTextPrimary"], c[s]):F2} (need 7)");
            Assert.True(Ratio(c["JevTextSecondary"], c[s]) >= 4.5, $"{theme}: secondary text on {s} = {Ratio(c["JevTextSecondary"], c[s]):F2} (need 4.5)");
        }
        // Hover/overlay surface: it is transient, so the bar is AA rather than AAA.
        Assert.True(Ratio(c["JevTextPrimary"], c["JevSurface3"]) >= 4.5);
        Assert.True(Ratio(c["JevTextSecondary"], c["JevSurface3"]) >= 4.5);
    }

    [Theory, MemberData(nameof(Themes))]
    public void Boundaries_focus_and_meaningful_graphics_reach_three_to_one(string theme)
    {
        var c = Colors(theme);
        foreach (var s in new[] { "JevSurface0", "JevSurface1", "JevSurface2" })
        {
            Assert.True(Ratio(c["JevBorderStrong"], c[s]) >= 3.0, $"{theme}: control boundary on {s} = {Ratio(c["JevBorderStrong"], c[s]):F2}");
            Assert.True(Ratio(c["JevFocus"], c[s]) >= 3.0, $"{theme}: focus ring on {s} = {Ratio(c["JevFocus"], c[s]):F2}");
        }
        foreach (var state in new[] { "Hot", "Warm", "Cold", "Suspended", "Virtual", "Archived" })
            Assert.True(Ratio(c["JevState" + state], c["JevSurface1"]) >= 3.0, $"{theme}: state dot {state} = {Ratio(c["JevState" + state], c["JevSurface1"]):F2}");
    }

    [Theory, MemberData(nameof(Themes))]
    public void Every_data_class_badge_has_readable_text_which_the_old_colours_did_not(string theme)
    {
        // The badge says how a page is treated. White on the old DarkSeaGreen was 2.15:1, on DarkOrange 2.33:1.
        var c = Colors(theme);
        foreach (var cls in new[] { "Public", "Unknown", "SignedIn", "Sensitive", "Secret", "Private" })
            Assert.True(Ratio(c["JevBadgeText"], c["JevBadge" + cls]) >= 4.5, $"{theme}: badge {cls} = {Ratio(c["JevBadgeText"], c["JevBadge" + cls]):F2}");
        Assert.True(Ratio(c["JevAccentOn"], c["JevAccent"]) >= 4.5, $"{theme}: text on accent = {Ratio(c["JevAccentOn"], c["JevAccent"]):F2}");
        Assert.True(Ratio(c["JevDangerText"], c["JevDanger"]) >= 4.5, $"{theme}: text on danger = {Ratio(c["JevDangerText"], c["JevDanger"]):F2}");
        // The production/staging strip is a safety signal. The old fills were 2.2 to 3.5 : 1 with white text.
        foreach (var env in new[] { "Staging", "Dev", "Other" })
            Assert.True(Ratio(c["JevDangerText"], c["JevEnv" + env]) >= 4.5, $"{theme}: environment {env} = {Ratio(c["JevDangerText"], c["JevEnv" + env]):F2}");
    }

    [Theory, MemberData(nameof(Themes))]
    public void Text_over_the_preview_scrim_is_readable_even_over_a_white_page(string theme)
    {
        var c = Colors(theme);
        var worst = Over(c["JevScrim"], "#FFFFFF");
        Assert.True(Ratio(c["JevBadgeText"], worst) >= 4.5, $"{theme}: scrim over white = {worst}, ratio {Ratio(c["JevBadgeText"], worst):F2}");
    }

    [Fact]
    public void The_two_themes_define_exactly_the_same_tokens()
    {
        Assert.Equal(Colors("Dark").Keys.OrderBy(k => k), Colors("Light").Keys.OrderBy(k => k));
        Assert.Equal(BrushKeys("Dark").OrderBy(k => k), BrushKeys("Light").OrderBy(k => k));
    }

    [Fact]
    public void High_contrast_supplies_every_brush_the_other_themes_do_and_uses_only_Windows_system_colours()
    {
        var hc = Theme("HighContrast");
        Assert.Equal(BrushKeys("Dark").OrderBy(k => k), BrushKeys("HighContrast").OrderBy(k => k));
        var used = hc.Descendants().SelectMany(e => e.Attributes()).Where(a => a.Name.LocalName == "Color").Select(a => a.Value).ToList();
        Assert.NotEmpty(used);
        // Only {ThemeResource SystemColor…}: the user's contrast theme is what they see, never a hue of ours.
        foreach (var v in used) Assert.Matches(@"^\{ThemeResource SystemColor\w+Color\}$", v);
        Assert.Empty(hc.Elements(P + "Color"));
    }

    [Fact]
    public void The_window_names_tokens_not_raw_colours_and_never_dims_text_with_opacity()
    {
        var xaml = File.ReadAllText(Path.Combine(Src(), "MainWindow.xaml"));
        Assert.Empty(Regex.Matches(xaml, @"(Background|BorderBrush|Foreground|Fill)=""#[0-9A-Fa-f]{6,8}"""));
        Assert.Empty(Regex.Matches(xaml, @"(Background|BorderBrush|Foreground|Fill)=""(White|Black|Red|Gray|Grey)"""));
        // Dimming text with Opacity produces a contrast nobody chose; the secondary text token exists for this.
        Assert.Empty(Regex.Matches(xaml, @"<TextBlock[^>]*\sOpacity=""0?\.\d+"""));
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void The_welcome_page_is_legible_in_both_schemes(string scheme)
    {
        var html = File.ReadAllText(Path.Combine(Src(), "WelcomePage.cs"));
        // dark = the :root block; light = the block inside @media (prefers-color-scheme: light)
        var block = scheme == "dark"
            ? Regex.Match(html, @":root \{ color-scheme:dark;(?<v>.*?)\}", RegexOptions.Singleline).Groups["v"].Value
            : Regex.Match(html, @"prefers-color-scheme: light\) \{\s*:root \{ color-scheme:light;(?<v>.*?)\}", RegexOptions.Singleline).Groups["v"].Value;
        Assert.NotEmpty(block);
        var v = Regex.Matches(block, @"--(?<k>[\w-]+):(?<c>#[0-9a-fA-F]{6})").ToDictionary(m => m.Groups["k"].Value, m => m.Groups["c"].Value);

        Assert.True(Ratio(v["text"], v["bg"]) >= 7.0, $"{scheme}: text on page = {Ratio(v["text"], v["bg"]):F2}");
        Assert.True(Ratio(v["muted"], v["bg"]) >= 4.5, $"{scheme}: muted on page = {Ratio(v["muted"], v["bg"]):F2}");
        foreach (var card in new[] { "card1", "card2" })
        {
            Assert.True(Ratio(v["body"], v[card]) >= 7.0, $"{scheme}: body on {card} = {Ratio(v["body"], v[card]):F2}");
            Assert.True(Ratio(v["text"], v[card]) >= 7.0, $"{scheme}: text on {card} = {Ratio(v["text"], v[card]):F2}");
            Assert.True(Ratio(v["teal"], v[card]) >= 4.5, $"{scheme}: teal on {card} = {Ratio(v["teal"], v[card]):F2}");
            Assert.True(Ratio(v["rose"], v[card]) >= 4.5, $"{scheme}: rose on {card} = {Ratio(v["rose"], v[card]):F2}");
        }
        Assert.True(Ratio(v["teal"], v["chip"]) >= 4.5, $"{scheme}: key chip = {Ratio(v["teal"], v["chip"]):F2}");
        Assert.True(Ratio(v["text"], v["btn"]) >= 7.0, $"{scheme}: button text = {Ratio(v["text"], v["btn"]):F2}");
        // The primary button is a teal-to-violet gradient with its own text colour: the worst end must still be readable.
        foreach (var end in new[] { "teal", "violet" })
            Assert.True(Ratio(v["btn-on"], v[end]) >= 4.5, $"{scheme}: primary button text on {end} = {Ratio(v["btn-on"], v[end]):F2}");
        foreach (var dot in new[] { "hot", "cold", "virt" })
            Assert.True(Ratio(v[dot], v["card1"]) >= 3.0, $"{scheme}: state dot {dot} = {Ratio(v[dot], v["card1"]):F2}");
    }

    [Fact]
    public void The_welcome_page_honours_reduced_motion_and_names_no_literal_colour_outside_its_two_schemes()
    {
        var html = File.ReadAllText(Path.Combine(Src(), "WelcomePage.cs"));
        Assert.Contains("prefers-reduced-motion: reduce", html);
        // Literal colours belong only in the two variable blocks; everything else must use the variables.
        var stripped = Regex.Replace(html, @":root \{[^}]*\}", "");
        Assert.Empty(Regex.Matches(stripped, @"(?<![\w-])#[0-9a-fA-F]{6}\b"));
    }

    [Fact]
    public void Nothing_in_the_shell_code_picks_a_named_colour()
    {
        foreach (var f in new[] { "MainWindow.xaml.cs", "MainWindow.Alpha.cs", "TabItem.cs", "MainWindow.Theme.cs" })
        {
            var code = File.ReadAllText(Path.Combine(Src(), f));
            Assert.DoesNotMatch(@"Microsoft\.UI\.Colors\.\w+|Color\.FromArgb\(", code);
        }
    }
}
