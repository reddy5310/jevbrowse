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

    // ---- layout tokens: radius, spacing, inset, elevation ----

    private static XElement AppResources() => XDocument.Load(Path.Combine(Src(), "App.xaml")).Descendants(P + "Application.Resources").Single();

    private static double First(string v) => double.Parse(v.Split(',')[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);

    private static Dictionary<string, string> LayoutTokens(string element) =>
        AppResources().Descendants(element == "Double" ? X + "Double" : P + element).Where(e => e.Attribute(X + "Key") is not null)
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value.Trim());

    [Fact]
    public void Text_that_names_no_colour_is_the_primary_text_token_by_an_implicit_style()
    {
        // Deleted once by an edit to a neighbouring block; every test stayed green and only a pixel diff noticed, because
        // dark text quietly became pure white instead of the token. Nothing else looked for this style.
        var implicitText = AppResources().Descendants(P + "Style").SingleOrDefault(e => (string?)e.Attribute("TargetType") == "TextBlock" && e.Attribute(X + "Key") is null);
        Assert.NotNull(implicitText);
        var fg = implicitText!.Elements(P + "Setter").Single(e => (string?)e.Attribute("Property") == "Foreground");
        Assert.Equal("{ThemeResource JevTextPrimaryBrush}", (string?)fg.Attribute("Value"));
    }

    [Fact]
    public void Radius_is_a_scale_that_only_grows()
    {
        var r = LayoutTokens("CornerRadius");
        var order = new[] { "JevRadiusDot", "JevRadiusSm", "JevRadiusControl", "JevRadiusCard", "JevRadiusPill", "JevRadiusPanel" };
        foreach (var k in order) Assert.True(r.ContainsKey(k), $"missing {k}");
        for (var i = 1; i < order.Length; i++)
            Assert.True(First(r[order[i]]) > First(r[order[i - 1]]), $"{order[i]} ({r[order[i]]}) must exceed {order[i - 1]} ({r[order[i - 1]]})");
    }

    [Fact]
    public void Spacing_sits_on_a_two_pixel_grid_and_only_grows()
    {
        var sp = LayoutTokens("Double").Where(kv => kv.Key.StartsWith("JevSpace")).OrderBy(kv => First(kv.Value)).ToList();
        Assert.NotEmpty(sp);
        foreach (var (k, v) in sp) Assert.True(First(v) % 2 == 0, $"{k} = {v} is off the 2 px grid");
        Assert.Equal(sp.Select(kv => kv.Key), sp.Select(kv => "JevSpace" + (int)First(kv.Value)));   // the name IS the value
        Assert.Equal(sp.Count, sp.Select(kv => kv.Value).Distinct().Count());
    }

    [Fact]
    public void Elevation_is_four_levels_each_higher_than_the_last()
    {
        var e = LayoutTokens("Double");
        var levels = new[] { "JevElevationGlow", "JevElevationRaised", "JevElevationSurface", "JevElevationPanel" };
        foreach (var k in levels) Assert.True(e.ContainsKey(k), $"missing {k}");
        for (var i = 1; i < levels.Length; i++)
            Assert.True(First(e[levels[i]]) > First(e[levels[i - 1]]), $"{levels[i]} must sit above {levels[i - 1]}");
    }

    [Fact]
    public void Every_elevation_the_window_asks_for_is_a_defined_level()
    {
        var defined = LayoutTokens("Double").Keys.ToHashSet();
        var xaml = File.ReadAllText(Path.Combine(Src(), "MainWindow.xaml"));
        var used = Regex.Matches(xaml, @"local:Elevation\.Level=""(\w+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(used);
        foreach (var level in used) Assert.True(defined.Contains("JevElevation" + level), $"local:Elevation.Level=\"{level}\" has no JevElevation{level} in App.xaml");
    }

    [Fact]
    public void The_window_markup_uses_layout_tokens_never_numbers()
    {
        var xaml = File.ReadAllText(Path.Combine(Src(), "MainWindow.xaml"));
        foreach (var attr in new[] { "CornerRadius", "Spacing", "ColumnSpacing", "RowSpacing", "Padding", "Margin", "Translation", "Shadow" })
            Assert.Empty(Regex.Matches(xaml, $@"(?<![\w.]){attr}=""[^{{""][^""]*"""));
    }

    [Fact]
    public void The_side_panel_is_reachable_and_named_for_keyboard_and_screen_reader_users()
    {
        var xaml = File.ReadAllText(Path.Combine(Src(), "MainWindow.xaml"));
        // Declared before the page so Tab reaches it straight after the toolbar, not after a whole web page.
        Assert.True(xaml.IndexOf("x:Name=\"SidePanel\"", StringComparison.Ordinal) is var panel and > 0 && panel < xaml.IndexOf("x:Name=\"PageSurface\"", StringComparison.Ordinal),
            "SidePanel must come before PageSurface in the markup: markup order is Tab order.");
        Assert.Matches(@"x:Name=""SidePanelClose""[^>]*AutomationProperties\.Name=""Close panel""", xaml);
        Assert.Matches(@"x:Name=""SidePanel""[^>]*KeyDown=""OnSidePanelKeyDown""", xaml);   // Esc
    }

    [Fact]
    public void Palette_and_help_stay_in_the_toolbar_when_the_sidebar_is_hidden()
    {
        var xaml = File.ReadAllText(Path.Combine(Src(), "MainWindow.xaml"));
        var toolbar = xaml[xaml.IndexOf("x:Name=\"Toolbar\"", StringComparison.Ordinal)..xaml.IndexOf("x:Name=\"SidePanel\"", StringComparison.Ordinal)];
        Assert.Contains("x:Name=\"MoreButton\"", toolbar);
        Assert.Contains("OnPalette", toolbar);
        Assert.Contains("OnHelp", toolbar);
        // Shield, Explain and Receipt are buttons in the toolbar, not items inside a menu.
        foreach (var name in new[] { "ShieldButton", "ExplainButton", "ReceiptButton" }) Assert.Contains($"<Button x:Name=\"{name}\"", toolbar);
    }

    [Fact]
    public void Decisions_stay_dialogs_and_reading_moves_to_the_panel()
    {
        var code = File.ReadAllText(Path.Combine(Src(), "MainWindow.xaml.cs")) + File.ReadAllText(Path.Combine(Src(), "MainWindow.Alpha.cs"));
        foreach (var handler in new[] { "OnShield", "OnExplain", "OnReceipt" })
            Assert.Matches($@"void {handler}\(object s, RoutedEventArgs e\) => OpenPanel\(", code);
        // A permission request must still block until answered: that is what makes ignoring it safe.
        Assert.Contains("PermissionDialogButton", code);
        Assert.Matches(@"Clear Browser Memory\?", code);
    }

    [Fact]
    public void The_agent_indicator_and_its_stop_are_in_the_toolbars_wrapping_bar_so_they_stay_reachable_at_any_width()
    {
        var xaml = File.ReadAllText(Path.Combine(Src(), "MainWindow.xaml"));
        var barStart = xaml.IndexOf("<local:WrapPanel x:Name=\"TrustBar\"", StringComparison.Ordinal);
        var bar = xaml[barStart..xaml.IndexOf("</local:WrapPanel>", barStart, StringComparison.Ordinal)];
        Assert.Contains("x:Name=\"AgentGroup\"", bar);
        Assert.Matches("x:Name=\"AgentBadge\"[^>]*Click=\"OnAgentActivity\"", bar);
        Assert.Matches("x:Name=\"AgentStopButton\"[^>]*Click=\"OnStopAgents\"", bar);
        // Hidden until a session runs (Collapsed also removes it from the tab order), and a plain button: Stop asks nothing first.
        Assert.Matches("x:Name=\"AgentGroup\"[^>]*Visibility=\"Collapsed\"", bar);
        var code = File.ReadAllText(Path.Combine(Src(), "MainWindow.Agents.cs"));
        var stop = code[code.IndexOf("async void OnStopAgents", StringComparison.Ordinal)..];
        Assert.DoesNotContain("ContentDialog", stop[..Math.Min(stop.Length, 700)]);   // the handler is short; nothing in it asks first
    }

    [Fact]
    public void Ending_a_restore_hides_its_indeterminate_progress_bar_because_it_animates_for_as_long_as_it_is_visible()
    {
        // A collapsed panel does not stop a visible indeterminate ProgressBar inside it: idle CPU went from ~1.4% to ~7% when nothing
        // collapsed it after start-up. Both places that end a restore must collapse the bar itself, not only its panel.
        var code = File.ReadAllText(Path.Combine(Src(), "MainWindow.xaml.cs"));
        var finish = code[code.IndexOf("private void FinishRestore", StringComparison.Ordinal)..];
        finish = finish[..finish.IndexOf("private ResourceId? _shortfallFor", StringComparison.Ordinal)];
        Assert.Contains("RestoreProgress.Visibility = Visibility.Collapsed", finish);
        var idle = code[code.IndexOf("private void UpdateIdlePanel", StringComparison.Ordinal)..];
        idle = idle[..idle.IndexOf("private void ShowRestoring", StringComparison.Ordinal)];
        Assert.Matches(@"_restoringId is null\)\s*\{[^}]*RestoreProgress\.Visibility = Visibility\.Collapsed", idle);
    }

    [Fact]
    public void Every_token_the_window_refers_to_exists()
    {
        var defined = AppResources().Descendants().Select(e => (string?)e.Attribute(X + "Key")).Where(k => k is not null).ToHashSet();
        var xaml = File.ReadAllText(Path.Combine(Src(), "MainWindow.xaml"));
        foreach (Match m in Regex.Matches(xaml, @"\{(?:Static|Theme)Resource (Jev\w+)\}"))
            Assert.True(defined.Contains(m.Groups[1].Value), $"MainWindow.xaml refers to {m.Groups[1].Value}, which App.xaml does not define");
    }

    [Fact]
    public void The_shell_code_takes_its_spacing_and_insets_from_the_tokens()
    {
        var src = Src();
        var scale = LayoutTokens("Double").Keys.Where(k => k.StartsWith("JevSpace")).Select(k => int.Parse(k["JevSpace".Length..])).ToHashSet();
        var insets = LayoutTokens("Thickness").Keys.ToHashSet();
        foreach (var f in new[] { "MainWindow.xaml.cs", "MainWindow.Alpha.cs", "MainWindow.Theme.cs", "TabItem.cs" })
        {
            var code = File.ReadAllText(Path.Combine(src, f));
            Assert.DoesNotMatch(@"\bSpacing = \d", code);
            Assert.DoesNotMatch(@"new Thickness\(\s*\d", code);
            foreach (Match m in Regex.Matches(code, @"Tokens\.Space\((\d+)\)"))
                Assert.True(scale.Contains(int.Parse(m.Groups[1].Value)), $"{f}: Tokens.Space({m.Groups[1].Value}) is not a step on the scale");
            foreach (Match m in Regex.Matches(code, @"Tokens\.Inset\(""(\w+)""\)"))
                Assert.True(insets.Contains(m.Groups[1].Value), $"{f}: Tokens.Inset(\"{m.Groups[1].Value}\") is not defined in App.xaml");
        }
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
