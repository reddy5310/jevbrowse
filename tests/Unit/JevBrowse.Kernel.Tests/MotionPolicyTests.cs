using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

public class MotionPolicyTests
{
    [Theory]
    [InlineData(MotionKind.Fast)]
    [InlineData(MotionKind.Base)]
    public void When_the_person_turns_animation_off_there_is_none_at_all(MotionKind kind) =>
        Assert.Equal(TimeSpan.Zero, MotionPolicy.Duration(kind, animationsEnabled: false));

    [Theory]
    [InlineData(MotionKind.Fast)]
    [InlineData(MotionKind.Base)]
    public void Every_transition_is_short_and_under_the_ceiling(MotionKind kind)
    {
        var d = MotionPolicy.Duration(kind, animationsEnabled: true);
        Assert.True(d > TimeSpan.Zero);
        Assert.True(d <= MotionPolicy.Ceiling, $"{kind} = {d.TotalMilliseconds} ms, ceiling {MotionPolicy.Ceiling.TotalMilliseconds} ms");
    }

    [Fact]
    public void Fast_is_faster_than_base() =>
        Assert.True(MotionPolicy.Duration(MotionKind.Fast, true) < MotionPolicy.Duration(MotionKind.Base, true));

    [Fact]
    public void Nothing_in_the_shell_starts_an_endless_animation_or_a_timer_for_decoration()
    {
        // "No added continuous rendering at idle" is a rule about the code as well as a measurement of the running app.
        var src = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "..", "src", "JevBrowse.App"));
        foreach (var f in Directory.GetFiles(src, "*.cs").Concat(Directory.GetFiles(src, "*.xaml")))
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("RepeatBehavior.Forever", text);
            Assert.DoesNotContain("IterationBehavior.Forever", text);
            Assert.DoesNotContain("IterationBehavior = AnimationIterationBehavior.Forever", text);
        }
    }

    private static string Here([System.Runtime.CompilerServices.CallerFilePath] string p = "") => p;
}
