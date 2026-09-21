using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace JevBrowse.Kernel.Tests;

/// <summary>
/// The window code cannot be run in a unit test, so the rules that keep it safe are checked in its source, next to the real-engine check that proves the behaviour
/// (`--nav-check`, `scripts/recovery-check.ps1`). Each rule here was a defect found in review; the test names say which.
/// </summary>
public class AppGuardStructureTests
{
    private static string App([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "src", "JevBrowse.App"));

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([App(), .. parts]));

    private static string Body(string src, string signature)
    {
        var i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, "not found: " + signature);
        var open = src.IndexOf('{', i);
        int depth = 0, j = open;
        for (; j < src.Length; j++) { if (src[j] == '{') depth++; else if (src[j] == '}' && --depth == 0) break; }
        return src[open..(j + 1)];
    }

    [Fact]
    public void The_engine_never_gets_to_open_its_own_popup_window()
    {
        var lease = Read("Renderer", "WebView2LeaseManager.cs");
        var handler = Regex.Match(lease, @"core\.NewWindowRequested \+= .*?\n        \};", RegexOptions.Singleline);
        Assert.True(handler.Success, "no NewWindowRequested handler: the default is an unmanaged window that skips admission, Shield and permissions");
        Assert.Contains("e.Handled = true", handler.Value);
    }

    [Fact]
    public void A_popup_is_decided_by_the_policy_and_opened_as_a_managed_tab_in_the_openers_workspace()
    {
        var w = Body(Read("MainWindow.xaml.cs"), "private void OnPopupRequested(");
        Assert.Contains("PopupPolicy.Decide(", w);
        Assert.Contains("OpenIn(src.WorkspaceId", w);
    }

    [Fact]
    public void The_address_bar_follows_the_page_in_front_but_never_overwrites_typing()
    {
        var w = Body(Read("MainWindow.xaml.cs"), "private void OnKernelChanged(");
        Assert.Matches(@"e\.Kind == ""navigated"" && _kernel\?\.Active\?\.Id == e\.Id && !IsEditingAddress", w);
    }

    [Fact]
    public void Startup_is_observed_and_the_window_is_not_usable_until_the_kernel_exists()
    {
        var src = Read("MainWindow.xaml.cs");
        Assert.DoesNotMatch(@"_ = InitAsync\(\);", src);           // an unobserved task hides every startup failure
        Assert.Contains("_ = InitSafeAsync();", src);
        Assert.Contains("Root.IsHitTestVisible = false;", src);
        Assert.Contains("Root.PreviewKeyDown += (_, e) => { if (!_ready) e.Handled = true; };", src);
        Assert.Contains("UnsupportedDatabaseVersionException", src);
        Assert.Contains("FatalStartupAsync", src);
    }

    [Fact]
    public void Startup_does_not_wait_forever_for_the_network()
    {
        var init = Body(Read("MainWindow.xaml.cs"), "private async Task InitAsync()");
        Assert.DoesNotMatch(@"await UpdateFilterListsAsync\(\);", init);
        Assert.Contains("Task.WhenAny(update, Task.Delay(", init);
    }

    [Fact]
    public void The_address_and_new_tab_handlers_cannot_throw_or_dereference_a_missing_kernel()
    {
        var src = Read("MainWindow.xaml.cs");
        var address = Body(src, "private async void OnAddressKeyDown(");
        Assert.Contains("AddressInput.Resolve(", address);
        Assert.DoesNotContain("new Uri(", address);
        Assert.Contains("catch (Exception", address);
        var newTab = Body(src, "private async void OnNewTab(");
        Assert.Contains("_kernel is null", newTab);
    }

    [Fact]
    public void Closing_a_tab_by_mouse_or_keyboard_uses_the_same_workspace_local_rule()
    {
        var mouse = Body(Read("MainWindow.xaml.cs"), "private async void OnCloseTab(");
        var keyboard = Body(Read("MainWindow.Alpha.cs"), "private async void OnCloseTabAccelerator(");
        Assert.Contains("CloseAndSelectNextAsync", mouse);
        Assert.Contains("CloseAndSelectNextAsync", keyboard);
        Assert.DoesNotContain("Tabs[^1]", mouse);   // the old rule activated the last tab of ANY workspace
    }

    [Fact]
    public void A_data_class_decision_is_keyed_by_host_and_applied_at_once()
    {
        var w = Body(Read("MainWindow.xaml.cs"), "private async void OnClassBadgeTapped(");
        Assert.Contains("SetDataClassOverrideForHost(", w);
        Assert.Contains("_kernel.ReapplyPolicy();", w);
        Assert.Contains("ForgetSite(", w);
        Assert.DoesNotContain("DataClassifier.Site(", Read("MainWindow.xaml.cs"));
    }

    [Fact]
    public void The_window_waits_for_agent_cleanup_before_it_closes_and_never_blocks_on_it()
    {
        var closing = Read("MainWindow.xaml.cs");
        Assert.Contains("await _agentHost.StopEndpointAsync().WaitAsync(", closing);
        Assert.DoesNotContain(".GetAwaiter().GetResult()", Read("..", "JevBrowse.AgentGateway", "LocalAgentHost.cs").Replace("JevBrowse.App", ""));
    }

    [Fact]
    public void Downloads_are_asked_about_for_private_sessions_and_never_allowed_for_agent_pages()
    {
        var d = Body(Read("MainWindow.SiteData.cs"), "private async Task<bool> ConfirmDownloadAsync(");
        Assert.Contains("if (agentPage) return false;", d);
        Assert.Contains("IsEphemeral()", d);
        Assert.Contains("stays on your computer after the Private session ends", Read("MainWindow.SiteData.cs"));
        Assert.Contains("lease.DownloadGate = ", Read("Renderer", "WebView2LeaseManager.cs"));
    }

    [Fact]
    public void The_resume_point_is_never_written_for_private_or_disposable_tabs_and_holds_ids_only()
    {
        var w = Body(Read("MainWindow.SiteData.cs"), "private void RememberResumePoint(");
        Assert.Contains("ContainerOf(t).IsEphemeral()) return;", w);
        Assert.DoesNotContain("Url", w);
    }

    [Fact]
    public void A_missing_WebView2_runtime_is_checked_before_anything_needs_it_and_explained()
    {
        var src = Read("MainWindow.xaml.cs");
        Assert.True(src.IndexOf("WebView2RuntimeAvailable(out var runtimeProblem)", StringComparison.Ordinal) < src.IndexOf("await InitAsync();", StringComparison.Ordinal));
        Assert.Contains("go.microsoft.com/fwlink/p/?LinkId=2124703", src);
    }
}
