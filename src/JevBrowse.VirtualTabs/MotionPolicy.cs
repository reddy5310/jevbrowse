namespace JevBrowse.VirtualTabs;

public enum MotionKind { Fast, Base }

/// <summary>
/// The rules for motion, as a pure function so they can be tested. Motion in this interface exists to explain a change
/// (a sleeping page becoming the live one, the sidebar leaving), never to decorate a still screen:
///   - it runs only in response to something the user or the system just did, and then stops;
///   - it is short, and there is a hard ceiling on how long any of it may take;
///   - when the person has asked Windows for no animation, there is none at all: the change is simply made.
/// </summary>
public static class MotionPolicy
{
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120);
    public static readonly TimeSpan Base = TimeSpan.FromMilliseconds(200);

    /// <summary>No transition in this interface may take longer than this.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromMilliseconds(250);

    public static TimeSpan Duration(MotionKind kind, bool animationsEnabled) =>
        !animationsEnabled ? TimeSpan.Zero : kind == MotionKind.Fast ? Fast : Base;
}
