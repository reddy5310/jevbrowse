using JevBrowse.Brain;
using JevBrowse.Domain;
using JevBrowse.TrustOS;

namespace JevBrowse.Brain.Tests;

public sealed class FakeDecisionProvider(bool configured = true) : IDecisionProvider
{
    public Provider Kind => Provider.Jev;
    public string Model => "jev-fake";
    public bool IsConfigured => configured;
    public List<(string State, IReadOnlyDictionary<string, Question> Questions)> Calls { get; } = [];
    public Func<IReadOnlyDictionary<string, Question>, DecisionAnswers> Answer { get; set; } = _ => new DecisionAnswers();
    public Task<DecisionAnswers> DecideAsync(string state, IReadOnlyDictionary<string, Question> questions, CancellationToken ct)
    {
        Calls.Add((state, questions));
        return Task.FromResult(Answer(questions));
    }
}

public class DecisionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch.AddDays(1);

    [Fact]
    public void Parses_real_jev_response_shape()
    {
        const string json = """
            {"model":"jev-1.13.0","answers":{
              "revisit":{"type":"score","score":1.63,"confidence":0.45,"legend":{"0":"Unlikely","1":"Possible","2":"Likely"},"probabilities":{"0":0.04,"1":0.29,"2":0.67}},
              "data_class":{"type":"choice","choice":"public","confidence":1.0,"probabilities":{"public":1.0,"authenticated":0.0,"sensitive":0.0}},
              "is_clutter_heavy":{"type":"noul","noul":0.49}},
             "usage":{"input_tokens":467,"output_tokens":76}}
            """;
        var a = JevDecisionProvider.Parse(json);
        Assert.Equal("jev-1.13.0", a.Model);
        Assert.Equal(467, a.InputTokens);
        Assert.Equal("public", a.Choices["data_class"].Choice);
        Assert.Equal(1.0, a.Choices["data_class"].Confidence);
        Assert.Equal(1.63, a.Scores["revisit"].Score);
        Assert.Equal(0.67, a.Scores["revisit"].Probabilities["2"]);
        Assert.Equal(0.49, a.Nouls["is_clutter_heavy"]);
    }

    [Fact]
    public async Task Judge_respects_every_gate_and_logs_numbers()
    {
        var jev = new FakeDecisionProvider { Answer = _ => { var a = new DecisionAnswers(); a.Choices["data_class"] = new("authenticated", 0.9, new Dictionary<string, double>()); return a; } };
        var log = new List<Decision>();
        var policy = new BrainPolicy();
        var r = new BrainRouter(new DefaultTrustPolicy(), policy, [], log.Add, () => T0, jev);
        var q = Judgements.PageQuestions();

        Assert.Null(await r.JudgeAsync("t", "state me@x.org", q, DataClass.Public, IdentityContainer.Personal, default));
        Assert.Equal("hard:ai_disabled", log[^1].Rule);
        policy.AiEnabled = true;
        Assert.Null(await r.JudgeAsync("t", "state", q, DataClass.Public, IdentityContainer.Personal, default));
        Assert.Equal("policy:cloud_disabled", log[^1].Rule);
        policy.CloudEnabled = true;
        // AI + Cloud on is consent to explicit actions, NOT to background calls: automatic needs its own switch.
        Assert.Null(await r.JudgeAsync("t", "state", q, DataClass.Public, IdentityContainer.Personal, default));
        Assert.Equal("policy:automatic_judgments_off", log[^1].Rule);
        policy.AutomaticJudgments = true;
        Assert.Null(await r.JudgeAsync("t", "state", q, DataClass.Secret, IdentityContainer.Personal, default));
        Assert.StartsWith("hard:", log[^1].Rule);
        Assert.Null(await r.JudgeAsync("t", "state", q, DataClass.Sensitive, IdentityContainer.Personal, default));
        Assert.Equal("policy:cloud_not_permitted_for_class", log[^1].Rule);
        Assert.Null(await r.JudgeAsync("t", "state", q, DataClass.Authenticated, IdentityContainer.Personal, default));   // Trust OS: automatic never sends AUTHENTICATED
        Assert.Equal("policy:cloud_not_permitted_for_class", log[^1].Rule);
        Assert.Null(await r.JudgeAsync("t", "state", q, DataClass.Public, IdentityContainer.Private, default));
        Assert.Empty(jev.Calls);

        var a = await r.JudgeAsync("classify", "state me@x.org", q, DataClass.Public, IdentityContainer.Personal, default);
        Assert.NotNull(a);
        Assert.Single(jev.Calls);
        Assert.DoesNotContain("me@x.org", jev.Calls[0].State);
        Assert.Equal(Provider.Jev, log[^1].Source);
        Assert.Contains("data_class=authenticated@0.90", log[^1].Output);
        Assert.True(log[^1].Redacted);
        // the audit record says what was asked, about which class, and whether a human clicked
        Assert.Equal("classify", log[^1].Task);
        Assert.Equal("Public", log[^1].DataClassName);
        Assert.True(log[^1].Automatic);
    }

    [Fact]
    public async Task Explicit_requests_may_reach_authenticated_pages_but_never_above_and_size_is_capped()
    {
        var jev = new FakeDecisionProvider();
        var log = new List<Decision>();
        var policy = new BrainPolicy { AiEnabled = true, CloudEnabled = true, MaxInputChars = 200 };   // AutomaticJudgments stays OFF
        var r = new BrainRouter(new DefaultTrustPolicy(), policy, [], log.Add, () => T0, jev);
        var q = Judgements.PageQuestions();

        Assert.NotNull(await r.JudgeAsync("rerank", "ok", q, DataClass.Authenticated, IdentityContainer.Personal, default, automatic: false));
        Assert.False(log[^1].Automatic);
        Assert.Null(await r.JudgeAsync("rerank", "ok", q, DataClass.Sensitive, IdentityContainer.Personal, default, automatic: false));
        Assert.Equal("policy:cloud_not_permitted_for_class", log[^1].Rule);
        Assert.Null(await r.JudgeAsync("rerank", new string('x', 500), q, DataClass.Public, IdentityContainer.Personal, default, automatic: false));
        Assert.Equal("hard:input_too_large", log[^1].Rule);                                            // previously unenforced on this path
        Assert.Single(jev.Calls);
    }

    [Fact]
    public async Task Question_text_is_redacted_too_not_just_the_state()
    {
        var jev = new FakeDecisionProvider();
        var policy = new BrainPolicy { AiEnabled = true, CloudEnabled = true };
        var r = new BrainRouter(new DefaultTrustPolicy(), policy, [], null, () => T0, jev);
        // a workspace named after a person's email, and a page-derived descriptor containing a card number
        var q = Judgements.WorkspaceQuestion(["alice@example.com's trip", "Work"]);
        var q2 = new Dictionary<string, Question> { ["e0"] = new NoulQuestion("Element 1: label=\"card 4111 1111 1111 1111\"") };

        await r.JudgeAsync("workspace", "state", q, DataClass.Public, IdentityContainer.Personal, default, automatic: false);
        await r.JudgeAsync("clutter", "state", q2, DataClass.Public, IdentityContainer.Personal, default, automatic: false);

        var sent = string.Join("\n", jev.Calls.SelectMany(c => c.Questions.Values.Select(v => v switch
        {
            ChoiceQuestion c2 => c2.Instructions + string.Join(" ", c2.Options.Keys) + string.Join(" ", c2.Options.Values),
            NoulQuestion n => n.Instructions,
            _ => "",
        })));
        Assert.DoesNotContain("4111", sent);
        Assert.DoesNotContain("alice@example.com", sent);           // keys are opaque and descriptions are scrubbed: the WHOLE payload is clean
        Assert.Contains("REDACTED:email", sent);
        Assert.Equal("Work", Judgements.WorkspaceNameFor("w1", ["alice@example.com's trip", "Work"]));   // and the answer still maps back
        Assert.Null(Judgements.WorkspaceNameFor("w9", ["Work"]));
        Assert.Null(Judgements.WorkspaceNameFor("Work", ["Work"]));                                      // a non-opaque key is not trusted
    }

    [Fact]
    public void Raise_only_raises_and_needs_confidence()
    {
        Assert.Equal(DataClass.Authenticated, Judgements.RaiseFrom(DataClass.Public, new("authenticated", 0.8, new Dictionary<string, double>())));
        Assert.Equal(DataClass.Sensitive, Judgements.RaiseFrom(DataClass.Authenticated, new("sensitive", 0.9, new Dictionary<string, double>())));
        Assert.Null(Judgements.RaiseFrom(DataClass.Sensitive, new("public", 1.0, new Dictionary<string, double>())));   // never lowers
        Assert.Null(Judgements.RaiseFrom(DataClass.Public, new("sensitive", 0.5, new Dictionary<string, double>())));   // too unsure
    }

    [Fact]
    public void Page_state_never_contains_content_only_structure()
    {
        var s = Judgements.PageState(new Uri("https://mail.example.com/inbox?secret=1"), "Inbox (3)", ["Primary", "Social"], PageSignals.PasswordField, 42, 3);
        Assert.Contains("Host: mail.example.com", s);
        Assert.Contains("Password field: yes", s);
        Assert.DoesNotContain("secret=1", s); // query string is not part of the state
    }
}
