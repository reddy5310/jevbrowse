namespace JevBrowse.Domain;

/// <summary>The zoom steps a person expects from a browser (the same ladder Chrome and Edge use), so Ctrl+ and Ctrl- land on familiar values.</summary>
public static class ZoomLevels
{
    public const double Min = 0.25, Max = 5.0, Default = 1.0;
    private static readonly double[] Ladder = [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0];

    /// <summary>The next step above (<paramref name="direction"/> &gt; 0) or below the current zoom; stays at the ends. A nonsense value counts as 100%.</summary>
    public static double Step(double current, int direction)
    {
        if (double.IsNaN(current) || double.IsInfinity(current) || current <= 0) current = Default;
        current = Math.Clamp(current, Min, Max);
        const double eps = 0.001;
        if (direction > 0) return Ladder.FirstOrDefault(z => z > current + eps, Max);
        if (direction < 0) return Ladder.LastOrDefault(z => z < current - eps, Min);
        return Default;
    }

    /// <summary>A stored value that cannot be trusted (edited, corrupt) becomes 100% rather than an unreadable page.</summary>
    public static double Sanitize(double stored) => double.IsNaN(stored) || double.IsInfinity(stored) || stored < Min || stored > Max ? Default : stored;

    public static string Label(double zoom) => $"{Math.Round(zoom * 100)}%";
}
