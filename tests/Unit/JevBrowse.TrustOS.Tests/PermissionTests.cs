using JevBrowse.TrustOS;

namespace JevBrowse.TrustOS.Tests;

public class PermissionTests
{
    private static readonly string[] Named =
    [
        "Geolocation", "Camera", "Microphone", "Notifications", "ClipboardRead", "OtherSensors",
        "MidiSystemExclusiveMessages", "MultipleAutomaticDownloads", "FileReadWrite", "LocalFonts", "Autoplay", "WindowManagement",
    ];

    [Fact]
    public void Every_permission_we_can_name_has_its_own_kind_so_no_two_share_a_grant()
    {
        var kinds = Named.Select(PermissionKinds.FromEngineName).ToList();
        Assert.DoesNotContain(PermissionKind.Other, kinds);
        Assert.Equal(kinds.Count, kinds.Distinct().Count());   // the bug: several of these all mapped to Other
    }

    [Fact]
    public void A_permission_we_cannot_name_becomes_Other()
    {
        Assert.Equal(PermissionKind.Other, PermissionKinds.FromEngineName("SomethingAddedInANewSdk"));
        Assert.Equal(PermissionKind.Other, PermissionKinds.FromEngineName(""));
    }

    [Fact]
    public void A_grant_for_one_permission_does_not_answer_a_different_one_from_the_same_site()
    {
        // "Allow automatic downloads for an hour" must not also allow reading and writing files.
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        PermissionGrant? Lookup(string site, PermissionKind kind) =>
            kind == PermissionKind.AutomaticDownloads ? new PermissionGrant(site, kind, true, now.AddHours(1)) : null;
        var policy = new PermissionPolicy(Lookup, () => now);

        Assert.Equal(PermissionVerdict.Allow, policy.Decide("site", PermissionKind.AutomaticDownloads));
        Assert.Equal(PermissionVerdict.Ask, policy.Decide("site", PermissionKind.FileAccess));
        Assert.Equal(PermissionVerdict.Ask, policy.Decide("site", PermissionKind.LocalFonts));
        Assert.Equal(PermissionVerdict.Ask, policy.Decide("site", PermissionKind.Autoplay));
    }

    [Fact]
    public void A_permission_we_cannot_name_is_asked_every_time_even_if_an_old_row_says_yes()
    {
        // Rows written before kinds were split can exist under Other. They must not be honoured: one remembered
        // "yes" for Other would cover every unrelated permission from that site.
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var policy = new PermissionPolicy((site, kind) => new PermissionGrant(site, kind, true, null), () => now);

        Assert.False(PermissionPolicy.MayRemember(PermissionKind.Other));
        Assert.Equal(PermissionVerdict.Ask, policy.Decide("site", PermissionKind.Other));
        Assert.Equal(PermissionVerdict.Allow, policy.Decide("site", PermissionKind.Camera));   // named kinds are unaffected
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Escape_or_the_close_button_refuses_only_this_request_whatever_else_was_chosen(bool durationSetToAnHour)
    {
        // Escape and the close button return the same dialog result, so they must mean the same, safe thing.
        Assert.Equal(PermissionChoice.BlockOnce, PermissionChoices.FromDialog(PermissionDialogButton.Dismissed, durationSetToAnHour));
    }

    [Fact]
    public void Only_an_explicit_button_can_make_a_permanent_decision_or_a_timed_allow()
    {
        Assert.Equal(PermissionChoice.Block, PermissionChoices.FromDialog(PermissionDialogButton.Block, false));
        Assert.Equal(PermissionChoice.AllowOnce, PermissionChoices.FromDialog(PermissionDialogButton.Allow, false));
        Assert.Equal(PermissionChoice.AllowForHour, PermissionChoices.FromDialog(PermissionDialogButton.Allow, true));
    }

    [Fact]
    public void Every_kind_has_a_specific_plain_phrase_except_the_unnamed_one()
    {
        foreach (var kind in Enum.GetValues<PermissionKind>().Where(k => k != PermissionKind.Other))
        {
            var phrase = PermissionKinds.Phrase(kind);
            Assert.NotEqual("use a browser feature", phrase);
            Assert.DoesNotContain("Other", phrase);
        }
        Assert.Equal("use a browser feature", PermissionKinds.Phrase(PermissionKind.Other));
    }

    [Fact]
    public void The_riskier_new_kinds_are_denied_by_default_not_asked()
    {
        var policy = new PermissionPolicy((_, _) => null);
        Assert.Equal(PermissionVerdict.Deny, policy.Decide("s", PermissionKind.MidiSystemExclusive));
        Assert.Equal(PermissionVerdict.Ask, policy.Decide("s", PermissionKind.FileAccess));
    }
}
