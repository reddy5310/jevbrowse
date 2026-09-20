using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace JevBrowse.App;

/// <summary>
/// Lays children out left to right and starts a new line when the next one would not fit. Rows of buttons need this: at a
/// larger Windows text size a row that fitted at 100% no longer does, and the two ways a fixed row can fail (a scroll bar
/// that hides controls, or a control squeezed to nothing) are both wrong for something a person has to be able to press.
/// </summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(0.0, OnChanged));
    public static readonly DependencyProperty LineSpacingProperty = DependencyProperty.Register(nameof(LineSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(0.0, OnChanged));

    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double LineSpacing { get => (double)GetValue(LineSpacingProperty); set => SetValue(LineSpacingProperty, value); }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { if (d is WrapPanel p) { p.InvalidateMeasure(); p.InvalidateArrange(); } }

    // A panel that lays children out by hand has to hear when one appears or goes away, or a child that starts Collapsed is never measured
    // again after it is shown. Each child is watched the first time the panel sees it (collapsed or not).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, object> _watched = new();

    private void Watch(UIElement child)
    {
        if (_watched.TryGetValue(child, out _)) return;
        _watched.Add(child, new object());
        child.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => { InvalidateMeasure(); InvalidateArrange(); });
    }

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, rowHeight = 0, widest = 0;
        foreach (var child in Children)
        {
            Watch(child);
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var s = child.DesiredSize;
            if (x > 0 && x + s.Width > available.Width)
            {
                widest = Math.Max(widest, x - Spacing);
                y += rowHeight + LineSpacing; x = 0; rowHeight = 0;
            }
            x += s.Width + Spacing;
            rowHeight = Math.Max(rowHeight, s.Height);
        }
        widest = Math.Max(widest, x > 0 ? x - Spacing : 0);
        return new Size(double.IsInfinity(available.Width) ? widest : Math.Min(widest, available.Width), y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var s = child.DesiredSize;
            if (x > 0 && x + s.Width > finalSize.Width) { y += rowHeight + LineSpacing; x = 0; rowHeight = 0; }
            child.Arrange(new Rect(x, y, s.Width, s.Height));
            x += s.Width + Spacing;
            rowHeight = Math.Max(rowHeight, s.Height);
        }
        return finalSize;
    }
}
