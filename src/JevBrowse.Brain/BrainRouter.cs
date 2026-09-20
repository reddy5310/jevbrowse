using System.Text.Json;
using JevBrowse.Domain;
using JevBrowse.TrustOS;

namespace JevBrowse.Brain;

/// <summary>User-controllable AI policy. Everything defaults to off (Constitution rules 2, 5).</summary>
public sealed class BrainPolicy
{
    /// <summary>Global kill switch. Off = the router answers every request from rules or refuses.</summary>
    public bool AiEnabled { get; set; }
    /// <summary>Cloud providers allowed at all. Local providers may still run when AiEnabled.</summary>
    public bool CloudEnabled { get; set; }
    /// <summary>Provider preference per task, first configured wins. Table A.8 defaults.</summary>
    public Dictionary<BrainTask, Provider[]> Preference { get; } = new()
    {
        [BrainTask.ScheduleHint] = [Provider.Rules, Provider.Local, Provider.Jev],
        [BrainTask.ClassifyPage] = [Provider.Jev, Provider.Local, Provider.OpenRouter],
        [BrainTask.SummarizePage] = [Provider.OpenRouter, Provider.Jev, Provider.Local],
        [BrainTask.ExplainError] = [Provider.OpenRouter, Provider.Jev, Provider.Local],
        [BrainTask.RerankSearch] = [Provider.Jev, Provider.Local],
    };
    public int MaxInputChars { get; set; } = 12_000;
    /// <summary>
    /// Separate from AiEnabled/CloudEnabled: allows Jev to be consulted WITHOUT a click (page classification after load,
    /// ad-slot judgement). Turning AI on is consent to explicit actions such as Ask, not to background calls.
    /// Default off.
    /// </summary>
    public bool AutomaticJudgments { get; set; }
}

/// <summary>
/// JevBrain (§11): a policy-controlled decision bus, not an assistant. Layers, highest first:
///   1 hard safety / user policy   2 deterministic rules   3 local scoring   4 Jev   5 LLM.
/// No lower layer can override a higher one; providers are only reached after Trust OS agrees and text is redacted.
/// </summary>
public sealed class BrainRouter : IBrainRouter
{
    private readonly ITrustPolicy _trust;
    private readonly BrainPolicy _policy;
    private readonly IReadOnlyList<IAiProvider> _providers;
    private readonly IDecisionProvider? _decisions;
    private readonly Action<Decision>? _log;
    private readonly Func<DateTimeOffset> _clock;

    public BrainRouter(ITrustPolicy trust, BrainPolicy policy, IReadOnlyList<IAiProvider> providers, Action<Decision>? log = null, Func<DateTimeOffset>? clock = null, IDecisionProvider? decisions = null)
    {
        _trust = trust;
        _policy = policy;
        _providers = providers;
        _decisions = decisions;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool HasDecisionProvider => _decisions?.IsConfigured == true;

    /// <summary>
    /// Layer 4: a typed judgement from Jev. Same gates as any cloud call (AI on, Cloud on, class ≤ AUTHENTICATED,
    /// never SECRET/EPHEMERAL), state is redacted, and the numeric answers are logged. Returns null when refused.
    /// </summary>
    public async Task<DecisionAnswers?> JudgeAsync(string task, string state, IReadOnlyDictionary<string, Question> questions, DataClass cls, IdentityContainer container, CancellationToken ct, bool automatic = true)
    {
        var now = _clock();
        Decision Deny(string rule) { var d = new Decision("", Provider.None, rule, _decisions?.Model, false, 0, state.Length, now, "brain/1", task, cls.ToString(), automatic); _log?.Invoke(d); return d; }

        // ---- layer 1: hard safety / user policy (same gates as every other cloud path) ----
        if (!_policy.AiEnabled) { Deny("hard:ai_disabled"); return null; }
        if (!_policy.CloudEnabled) { Deny("policy:cloud_disabled"); return null; }
        if (cls is DataClass.Secret or DataClass.Ephemeral || container.IsEphemeral()) { Deny($"hard:class_{cls.ToString().ToLower()}_never_leaves_device"); return null; }
        if (state.Length > _policy.MaxInputChars) { Deny("hard:input_too_large"); return null; }
        // Consent: AI on means the user may ASK; a background call needs its own switch.
        if (automatic && !_policy.AutomaticJudgments) { Deny("policy:automatic_judgments_off"); return null; }

        // Trust OS decides which classes may go to the cloud at all; an explicit user action may extend that to
        // AUTHENTICATED pages only (Table A.8), never above.
        var ctx = new ResourceContext(new Uri("about:blank"), cls, container);
        bool trustAllows = _trust.Evaluate(ctx, DataOperation.SendToCloudAI).Allowed;
        bool explicitException = !automatic && cls <= DataClass.Authenticated;
        if (!trustAllows && !explicitException) { Deny("policy:cloud_not_permitted_for_class"); return null; }
        if (_decisions is null || !_decisions.IsConfigured) { Deny("policy:no_decision_provider"); return null; }

        // Everything that leaves the machine is redacted: the state AND the question text (which can carry
        // page-derived descriptors or workspace names).
        var red = Redactor.Redact(state);
        int qRedactions = 0;
        string Scrub(string s) { var r = Redactor.Redact(s); qRedactions += r.Count; return r.Text; }
        var safeQuestions = questions.ToDictionary(kv => kv.Key, kv => kv.Value switch
        {
            ChoiceQuestion c => (Question)new ChoiceQuestion(Scrub(c.Instructions), c.Options.ToDictionary(o => o.Key, o => Scrub(o.Value))),
            ScoreQuestion s => new ScoreQuestion(Scrub(s.Instructions), s.Levels.Select(Scrub).ToList()),
            NoulQuestion n => new NoulQuestion(Scrub(n.Instructions)),
            var other => other,
        });
        var redactedCount = red.Count + qRedactions;
        try
        {
            var a = await _decisions.DecideAsync(red.Text, safeQuestions, ct);
            var summary = string.Join(" ", a.Choices.Select(kv => $"{kv.Key}={kv.Value.Choice}@{kv.Value.Confidence:0.00}")
                .Concat(a.Scores.Select(kv => $"{kv.Key}={kv.Value.Score:0.00}@{kv.Value.Confidence:0.00}"))
                .Concat(a.Nouls.Select(kv => $"{kv.Key}={kv.Value:0.00}")));
            _log?.Invoke(new Decision(summary, Provider.Jev, $"provider:jev:cloud:{task}", a.Model, redactedCount > 0, redactedCount, state.Length, now, "brain/1", task, cls.ToString(), automatic));
            return a;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            Deny($"provider_failed:jev:{ex.GetType().Name}");
            return null;
        }
    }

    public async Task<Decision> DecideAsync(DecisionRequest req, CancellationToken ct)
    {
        var d = await RouteAsync(req, ct);
        _log?.Invoke(d);
        return d;
    }

    private async Task<Decision> RouteAsync(DecisionRequest req, CancellationToken ct)
    {
        var now = _clock();
        Decision Deny(string rule) => new("", Provider.None, rule, null, false, 0, req.Input.Length, now, "brain/1", req.Task.ToString(), req.DataClass.ToString(), false);

        // ---- Layer 1: hard safety / user policy ----
        if (!_policy.AiEnabled) return Deny("hard:ai_disabled");
        if (req.DataClass is DataClass.Secret or DataClass.Ephemeral || req.Container.IsEphemeral())
            return Deny($"hard:class_{req.DataClass.ToString().ToLower()}_never_leaves_device");
        if (req.Input.Length > _policy.MaxInputChars) return Deny("hard:input_too_large");

        // ---- Layer 2: deterministic rules ----
        if (req.Task == BrainTask.ScheduleHint)
            return new("no_hint", Provider.Rules, "rules:schedule_is_local", null, false, 0, req.Input.Length, now, "brain/1", req.Task.ToString(), req.DataClass.ToString(), false);

        // Cloud gate: Trust OS says yes, OR the user explicitly asked on a non-sensitive page (Table A.8 override).
        var ctx = new ResourceContext(req.Url ?? new Uri("about:blank"), req.DataClass, req.Container);
        bool cloudAllowed = _policy.CloudEnabled &&
            (_trust.Evaluate(ctx, DataOperation.SendToCloudAI).Allowed ||
             (req.ExplicitUserAction && req.DataClass <= DataClass.Authenticated));
        bool needsExplicit = req.Task is BrainTask.SummarizePage or BrainTask.ExplainError;
        if (needsExplicit && !req.ExplicitUserAction) return Deny("policy:task_requires_explicit_user_action");

        // ---- Layers 3–5: first configured, permitted provider in preference order ----
        foreach (var kind in _policy.Preference.GetValueOrDefault(req.Task, []))
        {
            var p = _providers.FirstOrDefault(x => x.Kind == kind && x.IsConfigured);
            if (p is null) continue;
            bool isCloud = kind is Provider.Jev or Provider.OpenRouter or Provider.Custom;
            if (isCloud && !cloudAllowed) continue;

            var red = isCloud ? Redactor.Redact(req.Input) : new Redactor.Result(req.Input, 0, []);
            try
            {
                var output = await p.CompleteAsync(SystemPromptFor(req.Task), red.Text, ct);
                return new(output, kind, $"provider:{kind.ToString().ToLower()}:{(isCloud ? "cloud" : "local")}", p.Model, red.Count > 0, red.Count, req.Input.Length, now, "brain/1", req.Task.ToString(), req.DataClass.ToString(), false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                return new("", Provider.None, $"provider_failed:{kind.ToString().ToLower()}:{ex.GetType().Name}", p.Model, red.Count > 0, red.Count, req.Input.Length, now, "brain/1", req.Task.ToString(), req.DataClass.ToString(), false);
            }
        }
        return Deny(cloudAllowed ? "policy:no_provider_configured" : "policy:cloud_not_permitted_for_class");
    }

    private static string SystemPromptFor(BrainTask t) => t switch
    {
        BrainTask.SummarizePage => "You summarize web pages for a privacy-focused browser. Be concise: 5 bullet points max, then one line 'Key takeaway:'. Do not invent facts not in the text.",
        BrainTask.ExplainError => "You explain developer console/network errors. Give the likely cause first, then 2–3 concrete fixes. Be brief.",
        BrainTask.ClassifyPage => "Classify the page as one of: PUBLIC, AUTHENTICATED, SENSITIVE. Answer with the single word only.",
        BrainTask.RerankSearch => "Given a query and numbered candidates, answer with the candidate numbers in best-first order, comma-separated.",
        _ => "Answer briefly.",
    };
}
