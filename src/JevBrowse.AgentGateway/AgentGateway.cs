using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JevBrowse.Domain;
using JevBrowse.Renderer.Abstractions;
using JevBrowse.VirtualTabs;

namespace JevBrowse.AgentGateway;

/// <summary>Architecture §12.1. Everything an agent can do is declared here; anything not declared is denied.</summary>
public sealed class AgentManifest
{
    public string Agent { get; set; } = "";
    public string Workspace { get; set; } = "Agent";
    public List<string> AllowDomains { get; set; } = [];
    public List<DataClass> DenyDataClasses { get; set; } = [DataClass.Authenticated, DataClass.Sensitive, DataClass.Secret];
    public List<AgentAction> Actions { get; set; } = [AgentAction.Navigate, AgentAction.Read];
    /// <summary>"confirm" (default) asks the human; "deny" refuses; "allow" is only for throwaway containers.</summary>
    public string DestructiveActions { get; set; } = "confirm";
    public int MaxLivePages { get; set; } = 3;
    public int SessionMinutes { get; set; } = 60;
    public int MaxActions { get; set; } = 200;
    /// <summary>Container for a workspace the gateway creates. Disposable by default: the agent never sees user cookies.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))] public IdentityContainer Container { get; set; } = IdentityContainer.Disposable;
}

public sealed record AuditEntry(DateTimeOffset At, string Action, string Target, bool Allowed, string Reason);
public sealed record AgentRequest(AgentAction Action, string? Url = null, string? Selector = null, string? Text = null);
public sealed record AgentResponse(bool Ok, string Message, PageMap? Page = null, string? ScreenshotPath = null);

public sealed class AgentSession
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public required AgentManifest Manifest { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required ContextId WorkspaceId { get; init; }
    public int ActionsUsed { get; internal set; }
    public bool Closed { get; internal set; }
    public List<AuditEntry> Audit { get; } = [];
    public List<ResourceId> Pages { get; } = [];
    public ResourceId? Current { get; internal set; }
}

public interface IAgentGateway
{
    Task<AgentSession> OpenAsync(AgentManifest manifest, CancellationToken ct);
    Task<AgentResponse> ExecuteAsync(AgentSession session, AgentRequest request, CancellationToken ct);
    Task CloseAsync(AgentSession session, CancellationToken ct);
}

/// <summary>
/// Safe browser runtime for agents (§12): scope checks before any page is touched, lazy renderers under a quota,
/// a human in the loop for destructive actions, and an audit line for every request, allowed or not.
/// </summary>
public sealed partial class AgentGateway : IAgentGateway
{
    private readonly TabKernel _kernel;
    private readonly IRendererLeaseManager _leases;
    private readonly Func<AgentSession, AgentRequest, Task<bool>> _confirm;
    private readonly Action<AgentSession, AuditEntry>? _auditSink;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _screenshotDir;

    public TimeSpan LoadTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public AgentGateway(TabKernel kernel, IRendererLeaseManager leases, string screenshotDir,
        Func<AgentSession, AgentRequest, Task<bool>> confirmDestructive, Action<AgentSession, AuditEntry>? auditSink = null, Func<DateTimeOffset>? clock = null)
    {
        _kernel = kernel;
        _leases = leases;
        _screenshotDir = screenshotDir;
        _confirm = confirmDestructive;
        _auditSink = auditSink;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<AgentSession> OpenAsync(AgentManifest m, CancellationToken ct)
    {
        var ws = _kernel.Workspaces.FirstOrDefault(w => w.Name == m.Workspace);
        if (ws is null)
        {
            ws = _kernel.CreateWorkspace(m.Workspace);
            ws.Container = m.Container;
        }
        var now = _clock();
        var s = new AgentSession { Manifest = m, OpenedAt = now, ExpiresAt = now.AddMinutes(m.SessionMinutes), WorkspaceId = ws.Id };
        Record(s, "open", m.Agent, true, $"workspace '{ws.Name}' ({ws.Container}), {m.Actions.Count} actions, {m.AllowDomains.Count} domains, {m.MaxLivePages} live pages, {m.SessionMinutes} min");
        return Task.FromResult(s);
    }

    public async Task<AgentResponse> ExecuteAsync(AgentSession s, AgentRequest r, CancellationToken ct)
    {
        var target = r.Url ?? r.Selector ?? "";
        AgentResponse Deny(string why) { Record(s, r.Action.ToString(), target, false, why); return new(false, why); }

        // ---- scope: session, quota, action grant ----
        if (s.Closed) return Deny("session_closed");
        if (_clock() > s.ExpiresAt) { s.Closed = true; return Deny("session_expired"); }
        if (s.ActionsUsed >= s.Manifest.MaxActions) return Deny("action_quota_exhausted");
        if (!s.Manifest.Actions.Contains(r.Action)) return Deny($"action_not_granted:{r.Action}");
        s.ActionsUsed++;

        if (r.Action == AgentAction.Navigate)
        {
            if (!Uri.TryCreate(r.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) return Deny("bad_url");
            if (!DomainAllowed(s.Manifest, url.Host)) return Deny($"domain_not_allowed:{url.Host}");
            await EnsureQuotaAsync(s, ct);
            var tab = _kernel.TabsIn(s.WorkspaceId).FirstOrDefault(t => s.Pages.Contains(t.Id) && t.Url == url);
            if (tab is null)
            {
                await _kernel.SwitchWorkspaceAsync(s.WorkspaceId, ct);
                tab = _kernel.Open(url);
                s.Pages.Add(tab.Id);
            }
            await ActivateAndWaitAsync(tab.Id, ct);
            s.Current = tab.Id;
            Record(s, "navigate", url.ToString(), true, $"live={LiveAgentPages(s)}/{s.Manifest.MaxLivePages}");
            return new(true, "navigated");
        }

        // ---- everything else needs a current page that passes the data-class ceiling ----
        if (s.Current is not { } cur || _kernel.Tabs.All(t => t.Id != cur)) return Deny("no_current_page");
        var current = _kernel.Tabs.First(t => t.Id == cur);
        // Ceiling is judged on content, not container: a Disposable silo does not make a bank page less sensitive.
        var cls = _kernel.ContentClassOf(current);
        if (cls == DataClass.Secret) return Deny("hard:secret_page");
        if (s.Manifest.DenyDataClasses.Contains(cls)) return Deny($"data_class_denied:{cls}");
        if (!DomainAllowed(s.Manifest, current.Url.Host)) return Deny($"domain_not_allowed:{current.Url.Host}"); // page navigated away

        if (!_leases.TryGet(cur, out var lease)) { await EnsureQuotaAsync(s, ct); await ActivateAndWaitAsync(cur, ct); _leases.TryGet(cur, out lease); }
        if (lease is null) return Deny("renderer_unavailable");

        switch (r.Action)
        {
            case AgentAction.Read:
            {
                var map = await lease.GetPageMapAsync(ct);
                Record(s, "read", current.Url.ToString(), true, $"{map?.Links.Count ?? 0} links, {map?.Fields.Count ?? 0} fields");
                return new(true, "page map", map);
            }
            case AgentAction.Click:
            {
                if (string.IsNullOrWhiteSpace(r.Selector)) return Deny("missing_selector");
                if (LooksDestructive(r.Selector, r.Text))
                {
                    if (s.Manifest.DestructiveActions == "deny") return Deny("destructive_denied_by_manifest");
                    if (s.Manifest.DestructiveActions != "allow" && !await _confirm(s, r)) return Deny("destructive_not_confirmed_by_user");
                }
                var res = await lease.ClickAsync(r.Selector, ct);
                Record(s, "click", r.Selector, res.Ok, res.Message);
                return new(res.Ok, res.Message);
            }
            case AgentAction.TypeNonSecret:
            {
                if (string.IsNullOrWhiteSpace(r.Selector) || r.Text is null) return Deny("missing_selector_or_text");
                if (SecretSelector().IsMatch(r.Selector)) return Deny("hard:secret_field_selector");
                var res = await lease.TypeAsync(r.Selector, r.Text, ct);
                Record(s, "type", r.Selector, res.Ok, res.Message + $" ({r.Text.Length} chars)");
                return new(res.Ok, res.Message);
            }
            case AgentAction.Screenshot:
            {
                var dir = Path.Combine(_screenshotDir, s.Id);
                var cp = await lease.CaptureCheckpointAsync(dir, ct);
                Record(s, "screenshot", current.Url.ToString(), cp.ThumbnailPath is not null, cp.ThumbnailPath ?? "no image");
                return new(cp.ThumbnailPath is not null, cp.ThumbnailPath ?? "no image", null, cp.ThumbnailPath);
            }
            default: return Deny("unknown_action");
        }
    }

    public async Task CloseAsync(AgentSession s, CancellationToken ct)
    {
        foreach (var id in s.Pages.Where(id => _kernel.Tabs.Any(t => t.Id == id)))
            await _kernel.VirtualizeAsync(id, Cause.Scheduler, ct);
        s.Closed = true;
        Record(s, "close", s.Manifest.Agent, true, $"{s.ActionsUsed} actions, {s.Pages.Count} pages");
    }

    public int LiveAgentPages(AgentSession s) => _kernel.Tabs.Count(t => s.Pages.Contains(t.Id) && t.State.HasLiveRenderer());

    /// <summary>Lazy by design (§12): never more than MaxLivePages of the agent's pages hold a renderer.</summary>
    private async Task EnsureQuotaAsync(AgentSession s, CancellationToken ct)
    {
        while (LiveAgentPages(s) >= s.Manifest.MaxLivePages)
        {
            var victim = _kernel.Snapshot()
                .Where(x => s.Pages.Contains(x.Id) && x.State.HasLiveRenderer() && !x.IsActive && x.Protection == ProtectionFlags.None)
                .OrderBy(x => x.LastActive).FirstOrDefault()
                ?? _kernel.Snapshot().Where(x => s.Pages.Contains(x.Id) && x.State.HasLiveRenderer() && x.Protection == ProtectionFlags.None).OrderBy(x => x.LastActive).FirstOrDefault();
            if (victim is null) break;
            var r = await _kernel.VirtualizeAsync(victim.Id, Cause.Scheduler, ct);
            if (!r.Allowed) break;
            Record(s, "quota", victim.Id.ToString(), true, "virtualized LRU agent page");
        }
    }

    private async Task ActivateAndWaitAsync(ResourceId id, CancellationToken ct)
    {
        var loaded = new TaskCompletionSource();
        void OnEv(KernelEvent e) { if (e.Kind == "restored" && e.Id == id) loaded.TrySetResult(); }
        _kernel.Changed += OnEv;
        try
        {
            await _kernel.ActivateAsync(id, ct);
            await Task.WhenAny(loaded.Task, Task.Delay(LoadTimeout, ct));
        }
        finally { _kernel.Changed -= OnEv; }
    }

    private static bool DomainAllowed(AgentManifest m, string host) =>
        m.AllowDomains.Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));

    private static bool LooksDestructive(string selector, string? text) => Destructive().IsMatch(selector + " " + (text ?? ""));

    private void Record(AgentSession s, string action, string target, bool allowed, string reason)
    {
        var e = new AuditEntry(_clock(), action, target, allowed, reason);
        s.Audit.Add(e);
        _auditSink?.Invoke(s, e);
    }

    [GeneratedRegex(@"delete|remove|destroy|pay|purchase|checkout|deploy|publish|submit|confirm|send|transfer|drop|reset|revoke", RegexOptions.IgnoreCase)] private static partial Regex Destructive();
    [GeneratedRegex(@"password|passwd|pwd|otp|cvv|cvc|card|secret|token|ssn|aadhaar", RegexOptions.IgnoreCase)] private static partial Regex SecretSelector();
}
