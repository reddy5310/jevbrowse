using System.Collections.Concurrent;
using System.Text.Json;
using JevBrowse.DevSpace;
using JevBrowse.Domain;
using Microsoft.Web.WebView2.Core;

namespace JevBrowse.App.DevSpace;

public sealed class TabDevData
{
    public readonly ConcurrentQueue<ConsoleEntry> Console = new();
    public readonly ConcurrentQueue<NetEntry> Network = new();
    public readonly ConcurrentDictionary<string, DateTimeOffset> Started = new();
    public void Trim() { while (Console.Count > 500) Console.TryDequeue(out _); while (Network.Count > 1000) Network.TryDequeue(out _); }
}

/// <summary>
/// Optional module (§13): attaches DevTools-protocol listeners only when DevSpace is enabled, so general users pay
/// nothing. Collects console/network facts per tab; grouping and explanation happen elsewhere and on demand.
/// </summary>
public sealed class DevSpaceAdapter
{
    public bool Enabled { get; private set; }
    public ConcurrentDictionary<ResourceId, TabDevData> Data { get; } = new();
    public EnvironmentResolver Resolver { get; private set; } = new([]);
    public IReadOnlyList<ProjectConfig> Projects { get; private set; } = [];
    public string ProjectsPath { get; }

    public DevSpaceAdapter(string projectsPath)
    {
        ProjectsPath = projectsPath;
        ReloadProjects();
    }

    public void ReloadProjects()
    {
        Projects = ProjectStore.Load(ProjectsPath);
        Resolver = new EnvironmentResolver(Projects);
    }

    public void SetEnabled(bool on)
    {
        Enabled = on;
        if (!on) Data.Clear();   // nothing collected while it was on lingers after it is off
    }

    public async void Attach(CoreWebView2 core, ResourceId id)
    {
        if (!Enabled) return;
        var data = Data.GetOrAdd(id, _ => new TabDevData());
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
        }
        catch (Exception) { return; }

        // Listeners cannot be detached from an existing renderer, so each one checks Enabled: turning DevSpace off
        // (or leaving Developer mode) stops collection immediately, and data already held is dropped.
        core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, e) =>
        {
            if (!Enabled) return;
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                var type = doc.RootElement.GetProperty("type").GetString() ?? "log";
                var args = doc.RootElement.TryGetProperty("args", out var a) ? a.EnumerateArray().Select(x => x.TryGetProperty("value", out var v) ? v.ToString() : x.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "").ToList() : [];
                data.Console.Enqueue(new ConsoleEntry(DateTimeOffset.UtcNow, type, string.Join(" ", args), null, null));
                data.Trim();
            }
            catch (Exception) { }
        };
        core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, e) =>
        {
            if (!Enabled) return;
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                var d = doc.RootElement.GetProperty("exceptionDetails");
                var text = d.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var desc) ? desc.GetString() : d.GetProperty("text").GetString();
                var url = d.TryGetProperty("url", out var u) ? u.GetString() : null;
                var line = d.TryGetProperty("lineNumber", out var l) ? l.GetInt32() : (int?)null;
                data.Console.Enqueue(new ConsoleEntry(DateTimeOffset.UtcNow, "exception", text ?? "", url, line));
                data.Trim();
            }
            catch (Exception) { }
        };

        core.WebResourceRequested += (_, e) => { if (Enabled) data.Started[e.Request.Uri] = DateTimeOffset.UtcNow; };
        core.WebResourceResponseReceived += (_, e) =>
        {
            if (!Enabled) return;
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var url)) return;
            TimeSpan? dur = data.Started.TryRemove(e.Request.Uri, out var start) ? DateTimeOffset.UtcNow - start : null;
            data.Network.Enqueue(new NetEntry(DateTimeOffset.UtcNow, url, e.Request.Method, e.Response?.StatusCode ?? 0, dur, core.Source));
            data.Trim();
        };
    }

    public void Detach(ResourceId id) => Data.TryRemove(id, out _);
}
