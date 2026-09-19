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
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, AgentSession> _sessions = [];
    private readonly Func<AgentSession, Task>? _onOpened;
    private CancellationTokenSource? _cts;

    public LocalAgentHost(IAgentGateway gateway, Func<AgentSession, Task>? onSessionOpened = null)
    {
        _gateway = gateway;
        _onOpened = onSessionOpened;
        Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
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
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_listener.IsListening) _listener.Stop();
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
                    var manifest = await ReadAsync<AgentManifest>(req) ?? new AgentManifest();
                    var s = await _gateway.OpenAsync(manifest, ct);
                    lock (_sessions) _sessions[s.Id] = s;
                    if (_onOpened is not null) await _onOpened(s);
                    await Write(res, 200, new { id = s.Id, expiresAt = s.ExpiresAt, workspace = s.WorkspaceId.ToString() });
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
                    if (parts.Length == 2 && req.HttpMethod == "DELETE") { await _gateway.CloseAsync(s, ct); await Write(res, 200, new { closed = true }); return; }
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
