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
        Assert.Null(await r.JudgeAsync("t", "state", q, DataClass.Secret, IdentityContainer.Personal, default));
        Assert.StartsWith("hard:", log[^1].Rule);
        Assert.Null(await r.JudgeAsync("t", "state", q, DataClass.Sensitive, IdentityContainer.Personal, default));
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
