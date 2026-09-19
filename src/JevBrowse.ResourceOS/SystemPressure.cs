namespace JevBrowse.ResourceOS;

public enum PressureBand { Green, Yellow, Orange, Red }

/// <summary>One sample of the signals in Architecture §6. All values measured, none estimated.</summary>
public sealed record SystemPressure(
    long AvailableBytes,
    long TotalBytes,
    long ProcessGroupPrivateBytes,
    bool OnBattery,
    bool Metered,
    DateTimeOffset At)
{
    public double AvailableFraction => TotalBytes == 0 ? 1 : (double)AvailableBytes / TotalBytes;
}

/// <summary>
/// Maps available-memory fraction to a band with hysteresis (§20): a band is entered at one threshold and only
/// left when the signal moves a margin past it, so a signal hovering on a boundary cannot flap the scheduler.
/// </summary>
public sealed class PressureBandTracker
{
    // Enter thresholds on available fraction. Exit requires availableFraction >= threshold + Margin.
    private const double RedBelow = 0.08, OrangeBelow = 0.15, YellowBelow = 0.25, Margin = 0.03;

    public PressureBand Band { get; private set; } = PressureBand.Green;

    public PressureBand Update(double availableFraction)
    {
        var raw = availableFraction < RedBelow ? PressureBand.Red
                : availableFraction < OrangeBelow ? PressureBand.Orange
                : availableFraction < YellowBelow ? PressureBand.Yellow
                : PressureBand.Green;

        if (raw > Band) { Band = raw; return Band; }          // worsening: react immediately
        if (raw < Band)                                       // improving: need margin past the current band's entry
        {
            var exitAt = Band switch
            {
                PressureBand.Red => RedBelow + Margin,
                PressureBand.Orange => OrangeBelow + Margin,
                PressureBand.Yellow => YellowBelow + Margin,
                _ => 0,
            };
            if (availableFraction >= exitAt) Band = raw;
        }
        return Band;
    }
}
