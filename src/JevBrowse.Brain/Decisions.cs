using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JevBrowse.Brain;

/// <summary>Typed questions (TypeSafe Jev "System One"): a choice among options, a score on an ordered scale, or a yes-probability.</summary>
public abstract record Question(string Instructions);
public sealed record ChoiceQuestion(string Instructions, IReadOnlyDictionary<string, string> Options) : Question(Instructions);
public sealed record ScoreQuestion(string Instructions, IReadOnlyList<string> Levels) : Question(Instructions);
public sealed record NoulQuestion(string Instructions) : Question(Instructions);

public sealed record ChoiceAnswer(string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities);
public sealed record ScoreAnswer(double Score, double Confidence, IReadOnlyDictionary<string, double> Probabilities);

/// <summary>Answers keyed by question id. Numbers only: this is what makes Jev auditable in the decision log.</summary>
public sealed class DecisionAnswers
{
    public Dictionary<string, ChoiceAnswer> Choices { get; } = [];
    public Dictionary<string, ScoreAnswer> Scores { get; } = [];
    public Dictionary<string, double> Nouls { get; } = [];
    public string Model { get; init; } = "";
    public int InputTokens { get; init; }
}

/// <summary>A decision endpoint. Never prose; never reached before Trust OS and redaction.</summary>
public interface IDecisionProvider
{
    Provider Kind { get; }
    string Model { get; }
    bool IsConfigured { get; }
    Task<DecisionAnswers> DecideAsync(string state, IReadOnlyDictionary<string, Question> questions, CancellationToken ct);
}

/// <summary>TypeSafe Jev client: POST {base}/v1/systemone with a state and typed questions (docs.typesafe.ai).</summary>
public sealed class JevDecisionProvider : IDecisionProvider
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string? _key;

    public JevDecisionProvider(string? apiKey, Uri? baseUrl = null, string model = "jev-latest", HttpClient? http = null)
    {
        _key = apiKey;
        _base = baseUrl ?? new Uri("https://api.typesafe.ai/");
        Model = model;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public Provider Kind => Provider.Jev;
    public string Model { get; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_key);

    public static JevDecisionProvider FromEnvironment() =>
        new(Environment.GetEnvironmentVariable("JEV_API_KEY"),
            Uri.TryCreate(Environment.GetEnvironmentVariable("JEV_API_BASE"), UriKind.Absolute, out var b) ? b : null,
            Environment.GetEnvironmentVariable("JEV_MODEL") ?? "jev-latest");

    public async Task<DecisionAnswers> DecideAsync(string state, IReadOnlyDictionary<string, Question> questions, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Jev is not configured");
        var qs = new Dictionary<string, object>();
        foreach (var (id, q) in questions)
            qs[id] = q switch
            {
                ChoiceQuestion c => new { type = "choice", instructions = c.Instructions, criteria = c.Options },
                ScoreQuestion s => new { type = "score", instructions = s.Instructions, criteria = s.Levels },
                NoulQuestion n => new { type = "noul", instructions = n.Instructions },
                _ => throw new ArgumentOutOfRangeException(nameof(questions)),
            };
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "v1/systemone"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        req.Content = JsonContent.Create(new { model = Model, state, questions = qs });
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Jev {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        return Parse(body);
    }

    public static DecisionAnswers Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var a = new DecisionAnswers
        {
            Model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
            InputTokens = root.TryGetProperty("usage", out var u) && u.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0,
        };
        if (!root.TryGetProperty("answers", out var answers)) return a;
        foreach (var p in answers.EnumerateObject())
        {
            var v = p.Value;
            var type = v.TryGetProperty("type", out var t) ? t.GetString() : v.TryGetProperty("noul", out _) ? "noul" : v.TryGetProperty("choice", out _) ? "choice" : "score";
            switch (type)
            {
                case "noul": a.Nouls[p.Name] = v.GetProperty("noul").GetDouble(); break;
                case "choice": a.Choices[p.Name] = new(v.GetProperty("choice").GetString() ?? "", v.GetProperty("confidence").GetDouble(), Probs(v)); break;
                case "score": a.Scores[p.Name] = new(v.GetProperty("score").GetDouble(), v.GetProperty("confidence").GetDouble(), Probs(v)); break;
            }
        }
        return a;
    }

    private static Dictionary<string, double> Probs(JsonElement v)
    {
        var d = new Dictionary<string, double>();
        if (v.TryGetProperty("probabilities", out var ps)) foreach (var p in ps.EnumerateObject()) d[p.Name] = p.Value.GetDouble();
        return d;
    }
}
