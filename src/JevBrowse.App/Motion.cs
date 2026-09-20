using JevBrowse.VirtualTabs;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace JevBrowse.App;

/// <summary>
/// The one way this interface animates. Opacity fades run on the compositor, not on the UI thread, and only for the
/// length of the transition; nothing here is a timer, a loop or an infinite animation, so a still window costs nothing.
/// The person's Windows setting ("Show animations") decides whether any of it happens.
/// </summary>
internal static class Motion
{
    private static readonly UISettings Ui = new();
    private static readonly bool ForcedOff =
        string.Equals(Environment.GetEnvironmentVariable("JEVBROWSE_MOTION"), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>Read each time, not cached: the person can change it while the app is running.</summary>
    public static bool Enabled
    {
        get
        {
            if (ForcedOff) return false;
            try { return Ui.AnimationsEnabled; }
            catch (Exception) { return true; }   // cannot tell: the transitions are short and harmless
        }
    }

    /// <summary>Fade an element to an opacity. With animations off, the change is made at once and <paramref name="done"/> runs immediately.</summary>
    public static void Fade(UIElement e, float to, MotionKind kind, Action? done = null)
    {
        var duration = MotionPolicy.Duration(kind, Enabled);
        if (duration == TimeSpan.Zero) { e.Opacity = to; done?.Invoke(); return; }

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(e);
            var compositor = visual.Compositor;
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1f, to, compositor.CreateCubicBezierEasingFunction(new(0.2f, 0.8f), new(0.2f, 1f)));
            animation.Duration = duration;
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            visual.StartAnimation("Opacity", animation);
            batch.End();
            batch.Completed += (_, _) =>
            {
                e.Opacity = to;          // make the element agree with where the animation ended
                done?.Invoke();
            };
        }
        catch (Exception)
        {
            // Motion is explanation, not function: if the compositor refuses, make the change and carry on.
            e.Opacity = to; done?.Invoke();
        }
    }
}
