using JevBrowse.Domain;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JevBrowse.AgentGateway;

/// <summary>
/// Opt-in loopback HTTP surface so external agents (e.g. Claude Code) can use the gateway. Off by default.
/// Binds 127.0.0.1 only, requires a per-run bearer token, and exposes nothing beyond IAgentGateway.
///   POST /sessions                      body: AgentManifest      → { id }
///   POST /sessions/{id}/actions         body: AgentRequest       → AgentResponse
///   GET  /sessions/{id}/audit                                    → AuditEntry[]
///   DELETE /sessions/{id}
/// </summary>
public sealed class LocalAgentHost : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly IAgentGateway _gateway;
    private readonly AgentCeiling? _ceiling;
    private readonly Func<AgentManifest, AgentManifest, IReadOnlyList<string>, Task<bool>>? _approve;
    private readonly Func<AgentManifest, Task<bool>>? _approveScreenshots;

    /// <summary>
    /// Screenshots are experimental and default OFF. A request that names Screenshot, even within the approved ceiling, does not get it
    /// unless the person answers a separate question for this session. With no way to ask, it is dropped, never granted by default.
    /// </summary>
    private async Task<bool> GateScreenshotsAsync(AgentManifest effective, List<string> adjustments)
    {
        if (!effective.Actions.Contains(AgentAction.Screenshot)) return false;
        var approved = _approveScreenshots is not null && await _approveScreenshots(effective);
        if (!approved)
        {
            effective.Actions = effective.Actions.Where(a => a != AgentAction.Screenshot).ToList();
            adjustments.Add("screenshots (experimental) were not approved for this session: dropped");
        }
        return approved;
    }
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, AgentSession> _sessions = [];
    private readonly Func<AgentSession, Task>? _onOpened;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _sweeper;
    private int _sweeping;

    // Everything that reaches a renderer (WebView2 is single-threaded-apartment, UI-thread-only) must run on the thread that owns the window. The host is
    // created on that thread, so its context is remembered; the sweeper timer fires on a thread-pool thread and must hop back, and shutdown must not BLOCK
    // that thread while the cleanup it waits for needs it.
    private readonly SynchronizationContext? _owner = SynchronizationContext.Current;

    /// <summary>How often expired sessions are swept. Settable for tests.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Something in the background (the sweeper, an unawaited shutdown) failed. Never silent: the owner decides what to do with it.</summary>
    public event Action<Exception>? BackgroundFault;

    /// <summary>Runs on the owning thread when there is one, and completes when the work does. Exceptions surface through the task and <see cref="BackgroundFault"/>.</summary>
    private Task RunOwnedAsync(Func<Task> work)
    {
        if (_owner is null || SynchronizationContext.Current == _owner) return work();
        var done = new TaskCompletionSource();
        _owner.Post(async _ =>
        {
            try { await work(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }, null);
        return done.Task;
    }

    /// <param name="ceiling">
    /// What the user has approved. Requests are clamped to it (agents can narrow, never widen). With no ceiling the
    /// host refuses every session: possessing the token is not authority.
    /// </param>
    /// <param name="approve">Optional per-session human approval, shown the requested and the effective manifest.</param>
    public LocalAgentHost(IAgentGateway gateway, AgentCeiling? ceiling = null, Func<AgentManifest, AgentManifest, IReadOnlyList<string>, Task<bool>>? approve = null, Func<AgentSession, Task>? onSessionOpened = null, Func<AgentManifest, Task<bool>>? approveScreenshots = null)
    {
        _gateway = gateway;
        _ceiling = ceiling;
        _approve = approve;
        _approveScreenshots = approveScreenshots;
        _onOpened = onSessionOpened;
        Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// A grant made by the user directly (e.g. from the command palette): clamped to the ceiling, opened, and
    /// REGISTERED so the HTTP routes can find it. (Previously such sessions existed only in the gateway.)
    /// </summary>
    public async Task<(AgentSession Session, IReadOnlyList<string> Adjustments)> GrantAsync(AgentManifest requested, CancellationToken ct = default)
    {
        if (_ceiling is null) throw new InvalidOperationException("no agent ceiling configured");
        var clamped = _ceiling.Clamp(requested);
        var adjustments = clamped.Adjustments.ToList();
        var shots = await GateScreenshotsAsync(clamped.Effective, adjustments);
        var s = await _gateway.OpenAsync(clamped.Effective, ct);
        if (shots) _gateway.ApproveScreenshots(s);
        lock (_sessions) _sessions[s.Id] = s;
        if (_onOpened is not null) await _onOpened(s);
        return (s, adjustments);
    }

    /// <summary>Closes sessions past their expiry, and retries any whose pages did not release, even when the agent never calls again.</summary>
    public Task<int> SweepExpiredAsync(CancellationToken ct = default)
    {
        List<AgentSession> snapshot; lock (_sessions) snapshot = [.. _sessions.Values];
        return _gateway is AgentGateway gw ? gw.SweepExpiredAsync(snapshot, ct) : Task.FromResult(0);
    }

    /// <summary>Revoke one session now. Returns true only when its renderers were actually released.</summary>
    public async Task<bool> StopAsync(AgentSession s, CancellationToken ct = default) =>
        _gateway is AgentGateway gw ? await gw.StopAsync(s, ct) : false;

    /// <summary>Revoke every open session (the panel's "Stop all", and shutdown).</summary>
    public async Task<int> StopAllAsync(CancellationToken ct = default)
    {
        List<AgentSession> snapshot; lock (_sessions) snapshot = [.. _sessions.Values];
        int n = 0;
        foreach (var s in snapshot.Where(x => !x.CleanedUp)) { await StopAsync(s, ct); n++; }
        return n;
    }

    public string Token { get; }
    public int Port { get; private set; }
    public bool IsRunning => _listener.IsListening;
    public IReadOnlyCollection<AgentSession> Sessions => _sessions.Values;

    public void Start(int port = 0)
    {
        Port = port == 0 ? FreePort() : port;
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
        _sweeper = new System.Threading.Timer(_ => _ = SweepOnOwnerAsync(), null, SweepInterval, SweepInterval);
    }

    private async Task SweepOnOwnerAsync()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;   // the previous sweep is still releasing pages
        try { await RunOwnedAsync(async () => { await SweepExpiredAsync(); }); }
        catch (Exception ex) { BackgroundFault?.Invoke(ex); }
        finally { Volatile.Write(ref _sweeping, 0); }
    }

    /// <summary>
    /// Turns the endpoint off: refuses new requests at once, then revokes what it granted and waits for those pages to be released. Awaitable, so a caller
    /// on the UI thread can wait without blocking it.
    /// </summary>
    public async Task StopEndpointAsync(CancellationToken ct = default)
    {
        _sweeper?.Dispose();
        _cts?.Cancel();
        if (_listener.IsListening) _listener.Stop();
        await StopAllAsync(ct);
    }

    /// <summary>Non-blocking: the endpoint stops accepting now, and the revocation continues on the owning thread. Prefer <see cref="StopEndpointAsync"/> where a caller can wait.</summary>
    public void Stop()
    {
        _sweeper?.Dispose();
        _cts?.Cancel();
        if (_listener.IsListening) _listener.Stop();
        _ = RunOwnedAsync(async () => { await StopAllAsync(); }).ContinueWith(t => { if (t.Exception is { } e) BackgroundFault?.Invoke(e.GetBaseException()); }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested || !_listener.IsListening) { return; }
            catch (HttpListenerException) { continue; }
            _ = HandleAsync(ctx, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request; var res = ctx.Response;
        try
        {
            if (req.Headers["Authorization"] != "Bearer " + Token) { await Write(res, 401, new { error = "unauthorized" }); return; }
            var parts = req.Url!.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && parts[0] == "sessions")
            {
                if (parts.Length == 1 && req.HttpMethod == "POST")
                {
                    // The caller REQUESTS a manifest; it never chooses its own authority.
                    if (_ceiling is null) { await Write(res, 403, new { error = "no agent grant is configured: enable a ceiling in the Agents panel" }); return; }
                    var requested = await ReadAsync<AgentManifest>(req) ?? new AgentManifest();
                    var clamped = _ceiling.Clamp(requested);
                    if (clamped.Effective.AllowDomains.Count == 0 || clamped.Effective.Actions.Count == 0)
                    { await Write(res, 403, new { error = "nothing in the request is covered by the approved grant", adjustments = clamped.Adjustments }); return; }
                    if (_approve is not null && !await _approve(requested, clamped.Effective, clamped.Adjustments))
                    { await Write(res, 403, new { error = "the user declined this session" }); return; }
                    var adjustments = clamped.Adjustments.ToList();
                    var shots = await GateScreenshotsAsync(clamped.Effective, adjustments);
                    if (clamped.Effective.Actions.Count == 0)
                    { await Write(res, 403, new { error = "nothing in the request is covered once screenshots (experimental) were not approved", adjustments }); return; }
                    var s = await _gateway.OpenAsync(clamped.Effective, ct);
                    if (shots) _gateway.ApproveScreenshots(s);
                    lock (_sessions) _sessions[s.Id] = s;
                    if (_onOpened is not null) await _onOpened(s);
                    await Write(res, 200, new { id = s.Id, expiresAt = s.ExpiresAt, workspace = s.WorkspaceId.ToString(), granted = new { clamped.Effective.AllowDomains, clamped.Effective.Actions, clamped.Effective.MaxLivePages, clamped.Effective.SessionMinutes }, adjustments = clamped.Adjustments });
                    return;
                }
                if (parts.Length >= 2)
                {
                    AgentSession? s; lock (_sessions) _sessions.TryGetValue(parts[1], out s);
                    if (s is null) { await Write(res, 404, new { error = "no such session" }); return; }
                    if (parts.Length == 3 && parts[2] == "actions" && req.HttpMethod == "POST")
                    {
                        var r = await ReadAsync<AgentRequest>(req);
                        if (r is null) { await Write(res, 400, new { error = "bad request" }); return; }
                        var resp = await _gateway.ExecuteAsync(s, r, ct);
                        await Write(res, resp.Ok ? 200 : 403, resp);
                        return;
                    }
                    if (parts.Length == 3 && parts[2] == "audit" && req.HttpMethod == "GET") { await Write(res, 200, s.Audit); return; }
                    if (parts.Length == 2 && req.HttpMethod == "DELETE") { await _gateway.CloseAsync(s, ct); await Write(res, 200, new { closed = s.Closed, pagesReleased = s.CleanedUp }); return; }
                }
            }
            await Write(res, 404, new { error = "not found" });
        }
        catch (Exception ex) { try { await Write(res, 500, new { error = ex.GetType().Name }); } catch (Exception) { } }
    }

    private static async Task<T?> ReadAsync<T>(HttpListenerRequest req)
    {
        using var sr = new StreamReader(req.InputStream, req.ContentEncoding);
        var body = await sr.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(body) ? default : JsonSerializer.Deserialize<T>(body, Json);
    }

    private static async Task Write(HttpListenerResponse res, int status, object body)
    {
        res.StatusCode = status;
        res.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, Json));
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public void Dispose() { Stop(); _listener.Close(); }
}
