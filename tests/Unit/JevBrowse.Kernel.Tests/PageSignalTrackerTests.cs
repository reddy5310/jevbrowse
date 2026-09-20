using JevBrowse.Domain;
using JevBrowse.Renderer.Abstractions;
using Xunit;

namespace JevBrowse.Kernel.Tests;

/// <summary>A password or payment field anywhere on the page, in any frame at any depth, is on the person's screen and in any picture of it.</summary>
public class PageSignalTrackerTests
{
    private static PageSignalTracker<string> Tracker() => new();

    [Fact]
    public void A_password_field_in_a_frame_shows_up_in_what_is_on_screen()
    {
        var t = Tracker();
        t.AddFrame("iframe-1", PageSignals.PasswordField);
        Assert.True(t.Effective.HasFlag(PageSignals.PasswordField));
        Assert.Equal(PageSignals.None, t.Top);                   // the top document itself shows nothing
    }

    [Fact]
    public void Nested_and_sibling_frames_each_count_and_are_taken_back_one_at_a_time()
    {
        var t = Tracker();
        t.AddFrame("outer", PageSignals.None);
        t.AddFrame("inner-cross-origin", PageSignals.PasswordField);
        t.AddFrame("sibling", PageSignals.PaymentField);
        Assert.Equal(PageSignals.PasswordField | PageSignals.PaymentField, t.Effective);

        t.DropFrame("inner-cross-origin");                        // that frame went away (or replaced its document)
        Assert.Equal(PageSignals.PaymentField, t.Effective);      // the sibling's field is still on screen
        t.DropFrame("sibling");
        Assert.Equal(PageSignals.None, t.Effective);
    }

    [Fact]
    public void A_frame_may_only_contribute_secret_and_payment_never_signed_in_or_content_rendered()
    {
        var t = Tracker();
        t.AddFrame("widget", PageSignals.Authenticated | PageSignals.ContentRendered);
        Assert.Equal(PageSignals.None, t.Effective);              // a widget's "logout link" cannot re-classify the whole page
        t.AddFrame("widget", PageSignals.Authenticated | PageSignals.PasswordField);
        Assert.Equal(PageSignals.PasswordField, t.Effective);
    }

    [Fact]
    public void The_top_documents_signals_and_the_frames_add_up_and_neither_hides_the_other()
    {
        var t = Tracker();
        t.SetTop(PageSignals.Authenticated | PageSignals.ContentRendered);
        t.AddFrame("f", PageSignals.PaymentField);
        Assert.Equal(PageSignals.Authenticated | PageSignals.ContentRendered | PageSignals.PaymentField, t.Effective);
        t.DropFrame("f");
        Assert.Equal(PageSignals.Authenticated | PageSignals.ContentRendered, t.Effective);
    }

    [Fact]
    public void A_new_top_level_page_clears_the_old_pages_frames_too()
    {
        var t = Tracker();
        t.SetTop(PageSignals.PasswordField);
        t.AddFrame("f", PageSignals.PaymentField);
        t.Reset();
        Assert.Equal(PageSignals.None, t.Effective);
    }

    [Fact]
    public void A_field_appearing_later_in_a_frame_that_was_clean_is_picked_up()
    {
        var t = Tracker();
        t.AddFrame("late", PageSignals.None);                     // nothing yet
        Assert.Equal(PageSignals.None, t.Effective);
        t.AddFrame("late", PageSignals.PasswordField);            // the frame's script sees a field appear
        Assert.Equal(PageSignals.PasswordField, t.Effective);
    }

    [Fact]
    public void Dropping_a_frame_that_never_contributed_is_harmless()
    {
        var t = Tracker();
        t.DropFrame("nobody");
        Assert.Equal(PageSignals.None, t.Effective);
    }
}
