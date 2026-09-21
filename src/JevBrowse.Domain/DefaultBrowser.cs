using System.Runtime.Versioning;
using Microsoft.Win32;

namespace JevBrowse.Domain;

/// <summary>What another program (a mail client, a document, a shortcut) hands the browser on its command line.</summary>
public static class LaunchArgs
{
    /// <summary>
    /// The web address to open from a command line, or null. Only http and https are accepted (the same rule as the address bar), so a link from another app cannot
    /// make the browser open a file, a script or an internal page. Switches (starting with -) and the program's own path are skipped.
    /// </summary>
    public static Uri? ExtractUrl(IEnumerable<string?> args)
    {
        foreach (var raw in args)
        {
            var a = (raw ?? "").Trim().Trim('"');
            if (a.Length == 0 || a.Length > 8192 || a.StartsWith('-')) continue;
            if (Uri.TryCreate(a, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && !string.IsNullOrEmpty(u.Host)) return u;
        }
        return null;
    }
}

/// <summary>One registry value to write, relative to the base key. A null name is the key's default value.</summary>
public sealed record RegistryEntry(string Key, string? Name, string Value);

/// <summary>
/// Registers JevBrowse as a browser Windows can offer in Settings > Default apps. Everything is written under the CURRENT USER (no administrator rights, no other
/// account affected). Windows does not let a program make itself the default: registering only makes JevBrowse appear in the list, and the person chooses it there.
/// </summary>
public static class DefaultBrowser
{
    public const string AppKey = "JevBrowse";
    public const string UrlProgId = "JevBrowseURL";
    public const string HtmlProgId = "JevBrowseHTML";

    public static IReadOnlyList<RegistryEntry> Entries(string exePath)
    {
        var open = $"\"{exePath}\" \"%1\"";
        var cap = $@"Clients\StartMenuInternet\{AppKey}\Capabilities";
        return
        [
            new($@"Classes\{UrlProgId}", null, "JevBrowse URL"),
            new($@"Classes\{UrlProgId}", "URL Protocol", ""),
            new($@"Classes\{UrlProgId}\shell\open\command", null, open),
            new($@"Classes\{HtmlProgId}", null, "JevBrowse HTML Document"),
            new($@"Classes\{HtmlProgId}\shell\open\command", null, open),
            new($@"Clients\StartMenuInternet\{AppKey}", null, "JevBrowse"),
            new($@"Clients\StartMenuInternet\{AppKey}\shell\open\command", null, $"\"{exePath}\""),
            new(cap, "ApplicationName", "JevBrowse"),
            new(cap, "ApplicationDescription", "A private, low-memory browser with workspaces."),
            new($@"{cap}\URLAssociations", "http", UrlProgId),
            new($@"{cap}\URLAssociations", "https", UrlProgId),
            new($@"{cap}\FileAssociations", ".htm", HtmlProgId),
            new($@"{cap}\FileAssociations", ".html", HtmlProgId),
            new("RegisteredApplications", AppKey, $@"Software\{cap}"),
        ];
    }

    /// <summary>Writes the entries under <paramref name="baseKey"/> (normally <c>Software</c>) in the current user's registry.</summary>
    [SupportedOSPlatform("windows")]
    public static void Register(string exePath, string baseKey = "Software")
    {
        using var root = Registry.CurrentUser;
        foreach (var e in Entries(exePath))
        {
            using var k = root.CreateSubKey(baseKey + @"\" + e.Key, writable: true);
            k.SetValue(e.Name ?? "", e.Value, RegistryValueKind.String);
        }
    }

    /// <summary>Removes what <see cref="Register"/> wrote. Never touches the person's choice of default in Windows.</summary>
    [SupportedOSPlatform("windows")]
    public static void Unregister(string baseKey = "Software")
    {
        using var root = Registry.CurrentUser;
        foreach (var key in new[] { $@"Classes\{UrlProgId}", $@"Classes\{HtmlProgId}", $@"Clients\StartMenuInternet\{AppKey}" })
            root.DeleteSubKeyTree(baseKey + @"\" + key, throwOnMissingSubKey: false);
        using var reg = root.OpenSubKey(baseKey + @"\RegisteredApplications", writable: true);
        reg?.DeleteValue(AppKey, throwOnMissingValue: false);
    }

    [SupportedOSPlatform("windows")]
    public static bool IsRegistered(string baseKey = "Software")
    {
        using var k = Registry.CurrentUser.OpenSubKey(baseKey + @"\RegisteredApplications");
        return k?.GetValue(AppKey) is string;
    }
}
