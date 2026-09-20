using JevBrowse.Domain;

namespace JevBrowse.Renderer.Abstractions;

/// <summary>
/// What a page shows is the sum of ALL its documents: the top one and every frame, nested and cross-origin alike. A password or payment
/// field in an iframe is on the person's screen exactly as one in the page is, and a picture of the page contains it. This keeps each
/// frame's contribution separately so that a frame going away (or replacing its document) takes only its own back, and a reset on
/// navigation clears everything.
/// <para>
/// Frames may only contribute the two "secret on screen" flags. A frame cannot lower the class (it never removes anything) and its
/// "signed in" or "content rendered" evidence is not allowed to raise or settle the page's class on its own.
/// </para>
/// </summary>
public sealed class PageSignalTracker<TFrame> where TFrame : notnull
{
    private const PageSignals FromFrames = PageSignals.PasswordField | PageSignals.PaymentField;
    private readonly Dictionary<TFrame, PageSignals> _frames = [];

    /// <summary>The top-level document's own signals.</summary>
    public PageSignals Top { get; private set; }

    /// <summary>Everything that is visible on the page: the top document plus every frame.</summary>
    public PageSignals Effective
    {
        get { var f = PageSignals.None; foreach (var v in _frames.Values) f |= v; return Top | f; }
    }

    public void SetTop(PageSignals signals) => Top = signals;

    public void AddFrame(TFrame frame, PageSignals flags)
    {
        flags &= FromFrames;
        if (flags == PageSignals.None) return;
        _frames[frame] = _frames.GetValueOrDefault(frame) | flags;
    }

    public void DropFrame(TFrame frame) => _frames.Remove(frame);

    /// <summary>A new top-level document: nothing of the old page, in it or in its frames, is on screen any more.</summary>
    public void Reset() { Top = PageSignals.None; _frames.Clear(); }
}
