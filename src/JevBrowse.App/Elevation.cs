using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace JevBrowse.App;

/// <summary>
/// Depth as a named level: <c>local:Elevation.Level="Panel"</c>. The Z depth comes from the JevElevation* numbers in
/// App.xaml, so how far a surface sits above the one beneath it is stated once. (A Style cannot set Translation, which
/// is not a dependency property, hence an attached property.)
/// </summary>
public static class Elevation
{
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.RegisterAttached("Level", typeof(string), typeof(Elevation), new PropertyMetadata(null, OnLevelChanged));

    public static string? GetLevel(DependencyObject o) => (string?)o.GetValue(LevelProperty);
    public static void SetLevel(DependencyObject o, string? value) => o.SetValue(LevelProperty, value);

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el || e.NewValue is not string level) return;
        var z = Application.Current.Resources.TryGetValue("JevElevation" + level, out var v) && v is double depth ? (float)depth : 0f;
        if (Application.Current.Resources.TryGetValue("JevShadow", out var shadow) && shadow is Shadow s) el.Shadow = s;
        el.Translation = new Vector3(0, 0, z);
    }
}
