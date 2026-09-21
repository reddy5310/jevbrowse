using JevBrowse.Domain;
using Microsoft.Win32;

namespace JevBrowse.Kernel.Tests;

public class DefaultBrowserTests
{
    [Theory]
    [InlineData("https://example.org/a?b=1", "https://example.org/a?b=1")]
    [InlineData("\"http://example.org/\"", "http://example.org/")]
    public void A_web_address_on_the_command_line_is_opened(string arg, string expected) =>
        Assert.Equal(expected, LaunchArgs.ExtractUrl(["C:\\x\\JevBrowse.App.exe", "--flag", arg])!.ToString());

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///c:/windows/system32/cmd.exe")]
    [InlineData("jev://welcome")]
    [InlineData("ftp://example.org/x")]
    [InlineData("c:\\temp\\page.html")]
    [InlineData("--ui-shot")]
    [InlineData("")]
    public void Anything_that_is_not_a_web_address_is_ignored(string arg) => Assert.Null(LaunchArgs.ExtractUrl([arg, null]));

    [Fact]
    public void The_registration_is_current_user_only_covers_web_and_html_and_quotes_the_path()
    {
        var exe = @"C:\Program Files\JevBrowse\JevBrowse.App.exe";
        var e = DefaultBrowser.Entries(exe);
        Assert.All(e, x => Assert.DoesNotContain("HKEY_LOCAL_MACHINE", x.Key, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(e, x => x.Key.EndsWith(@"URLAssociations") && x.Name == "http" && x.Value == DefaultBrowser.UrlProgId);
        Assert.Contains(e, x => x.Key.EndsWith(@"URLAssociations") && x.Name == "https");
        Assert.DoesNotContain(e, x => x.Key.Contains("FileAssociations") || x.Key.Contains("HTML"));   // local files are not opened by the launch path, so none are claimed
        Assert.Contains(e, x => x.Key.EndsWith(@"shell\open\command") && x.Value == $"\"{exe}\" \"%1\"");   // a path with spaces stays one argument
        Assert.Contains(e, x => x.Key == "RegisteredApplications" && x.Name == "JevBrowse");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Registering_writes_the_entries_and_unregistering_removes_them_all()
    {
        // A scratch area under the current user's Software key, removed at the end: this never touches the real browser registrations.
        var scratch = @"Software\JevBrowseTest-" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.False(DefaultBrowser.IsRegistered(scratch));
            DefaultBrowser.Register(@"C:\x\JevBrowse.App.exe", scratch);
            Assert.True(DefaultBrowser.IsRegistered(scratch));
            using (var k = Registry.CurrentUser.OpenSubKey(scratch + @"\Classes\JevBrowseURL\shell\open\command"))
                Assert.Equal("\"C:\\x\\JevBrowse.App.exe\" \"%1\"", k!.GetValue(""));
            DefaultBrowser.Register(@"C:\x\JevBrowse.App.exe", scratch);                 // twice is the same as once
            DefaultBrowser.Unregister(scratch);
            Assert.False(DefaultBrowser.IsRegistered(scratch));
            using var left = Registry.CurrentUser.OpenSubKey(scratch + @"\Classes\JevBrowseURL");
            Assert.Null(left);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false); }
    }
}
