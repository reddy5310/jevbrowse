using JevBrowse.Domain;
using JevBrowse.ResourceOS;

namespace JevBrowse.VirtualTabs;

/// <summary>What the Explain dialog needs to know about one tab. Plain facts; no UI types.</summary>
public sealed record ExplainFacts(
    ResourceState State, bool IsForeground, ScheduledAction? Decision, PressureBand? Band, int MostAwake, int AwakeNow,
    IReadOnlyList<string> KeepAwakeReasons);

public sealed record ExplainView(string Headline, IReadOnlyList<string> Lines);

/// <summary>
/// Turns the scheduler's decision record into the answer to the question people actually ask: "why is this tab awake, or
/// asleep?" The dialog used to print the raw record in a monospace font ("Current band: Yellow, live budget 6, live now
/// 1", "inactive_minutes: 4", "trigger: over_budget"), which answers a question nobody asked.
/// </summary>
public static class ExplainText
{
    public static ExplainView Build(ExplainFacts f)
    {
        var lines = new List<string>();
        var awake = f.State.HasLiveRenderer();

        var headline = f.IsForeground && awake ? "This is the tab you are looking at, so it stays awake."
            : awake ? "This tab is awake in the background."
            : "This tab is asleep. It is not using memory, and it wakes when you open it.";

        if (f.Decision is { } d)
        {
            var r = d.Reasons;
            string R(string key) => r.TryGetValue(key, out var v) ? v : "";
            var trigger = R("trigger") == "over_budget"
                ? $"you had more tabs open than the {f.MostAwake} JevBrowse keeps awake at once"
                : $"it had not been used for {R("inactive_minutes")} minutes";

            if (r.ContainsKey("skipped"))
            {
                var why = R("skipped") switch
                {
                    "protected" => f.KeepAwakeReasons.Count > 0 ? $"it is {string.Join(", ", f.KeepAwakeReasons)}" : "something on the page needs it awake",
                    "min_residency" => "it was woken up only a moment ago",
                    "cooldown" => "it was put to sleep and woken again very recently",
                    "hibernation_window_limit" => "several tabs have gone to sleep recently and JevBrowse is pacing itself; it may sleep soon",
                    _ => "of a rule that protects it",
                };
                lines.Add($"JevBrowse considered putting it to sleep ({trigger}) but did not, because {why}.");
            }
            else if (!awake) lines.Add($"It was put to sleep because {trigger}.");
        }
        else if (f.KeepAwakeReasons.Count > 0)
            lines.Add($"It will not go to sleep on its own while it is {string.Join(", ", f.KeepAwakeReasons)}.");
        else if (awake)
            lines.Add("JevBrowse has not needed to change anything about this tab.");

        if (f.Band is { } band)
        {
            var memory = band switch
            {
                PressureBand.Green => "Memory is comfortable",
                PressureBand.Yellow => "Memory use is moderate",
                PressureBand.Orange => "Memory is getting tight",
                _ => "Memory is very tight",
            };
            lines.Add($"{memory}. JevBrowse can keep up to {f.MostAwake} tabs awake right now, and {(f.AwakeNow == 1 ? "1 is" : $"{f.AwakeNow} are")} awake.");
        }
        return new ExplainView(headline, lines);
    }

    public const string Title = "Why is this tab awake or asleep?";
}
