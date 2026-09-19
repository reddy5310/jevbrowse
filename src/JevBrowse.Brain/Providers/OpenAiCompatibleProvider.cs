using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace JevBrowse.Brain.Providers;

/// <summary>
/// Chat-completions client for any OpenAI-compatible endpoint. Used for OpenRouter and (pending confirmation of
/// its API shape) Jev. Never called unless BrainRouter has cleared the request; receives redacted text only.
/// </summary>
public sealed class OpenAiCompatibleProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly Uri? _base;
    private readonly string? _key;

    public OpenAiCompatibleProvider(Provider kind, Uri? baseUrl, string? apiKey, string model, HttpClient? http = null, string? referer = null)
    {
        Kind = kind;
        _base = baseUrl;
        _key = apiKey;
        Model = model;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (referer is not null) { _http.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", referer); _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", "JevBrowse"); }
    }

    public Provider Kind { get; }
    public string Model { get; }
    public bool IsConfigured => _base is not null && !string.IsNullOrWhiteSpace(_key) && !string.IsNullOrWhiteSpace(Model);

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException($"{Kind} is not configured");
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_base!, "chat/completions"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        req.Content = JsonContent.Create(new
        {
            model = Model,
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
            max_tokens = 600,
            temperature = 0.2,
        });
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{Kind} {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    /// <summary>Build from environment. Keys are never stored by JevBrowse; they come from the OS environment.</summary>
    public static IReadOnlyList<IAiProvider> FromEnvironment()
    {
        var list = new List<IAiProvider>();
        var orKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var orModel = Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? "openai/gpt-4o-mini";
        list.Add(new OpenAiCompatibleProvider(Provider.OpenRouter, new Uri("https://openrouter.ai/api/v1/"), orKey, orModel, referer: "https://github.com/reddy5310/jevbrowse"));

        // Jev is not a chat model; it is wired as an IDecisionProvider (JevDecisionProvider.FromEnvironment).
        return list;
    }
}
