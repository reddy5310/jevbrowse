using JevBrowse.Brain;
using JevBrowse.Domain;
using JevBrowse.TrustOS;

namespace JevBrowse.Brain.Tests;

public sealed class FakeProvider(Provider kind, bool configured = true) : IAiProvider
{
    public Provider Kind => kind;
    public string Model => kind + "-model";
    public bool IsConfigured => configured;
    public List<string> Received { get; } = [];
    public string Reply { get; set; } = "ok";
    public bool Throw { get; set; }
    public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        if (Throw) throw new HttpRequestException("boom");
        Received.Add(user);
        return Task.FromResult(Reply);
    }
}

public class BrainRouterTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch.AddDays(1);

    private static (BrainRouter router, FakeProvider or, FakeProvider jev, List<Decision> log, BrainPolicy policy) Make(bool ai = true, bool cloud = true)
    {
        var or = new FakeProvider(Provider.OpenRouter);
        var jev = new FakeProvider(Provider.Jev);
        var log = new List<Decision>();
        var policy = new BrainPolicy { AiEnabled = ai, CloudEnabled = cloud };
        var router = new BrainRouter(new DefaultTrustPolicy(), policy, [or, jev], log.Add, () => T0);
        return (router, or, jev, log, policy);
    }

    private static DecisionRequest Req(BrainTask task, DataClass cls, bool explicitAction = true, IdentityContainer c = IdentityContainer.Personal) =>
        new(task, "Some page text with contact me@example.com and card 4111 1111 1111 1111.", cls, c, explicitAction, new Uri("https://x.test/"));

    [Fact]
    public async Task A_page_we_could_not_assess_is_never_sent_even_on_an_explicit_request()
    {
        // Both routing paths used `cls <= DataClass.Authenticated`, which started including UNKNOWN the moment
        // Unknown was inserted at 1. AI on, cloud on, providers configured, user explicitly asking: still refused.
        var (r, or, jev, _, _) = Make();
        var chat = await r.DecideAsync(Req(BrainTask.SummarizePage, DataClass.Unknown, explicitAction: true), default);
        Assert.True(chat.WasDenied);
        Assert.Equal("policy:cloud_not_permitted_for_class", chat.Rule);
        Assert.Empty(or.Received);
        Assert.Empty(jev.Received);

        // The typed-decision path is a separate gate and had the same hole.
        var decisions = new FakeDecisionProvider();
        var typed = new BrainRouter(new DefaultTrustPolicy(), new BrainPolicy { AiEnabled = true, CloudEnabled = true }, [], null, () => T0, decisions);
        var answers = await typed.JudgeAsync("classify", "page text", new Dictionary<string, Question>(), DataClass.Unknown,
            IdentityContainer.Personal, default, automatic: false);
        Assert.Null(answers);
        Assert.Empty(decisions.Calls);
    }

    [Fact]
    public async Task Kill_switch_stops_everything_and_still_logs()
    {
        var (r, or, _, log, _) = Make(ai: false);
        var d = await r.DecideAsync(Req(BrainTask.SummarizePage, DataClass.Public), default);
        Assert.True(d.WasDenied);
        Assert.Equal("hard:ai_disabled", d.Rule);
        Assert.Empty(or.Received);
        Assert.Single(log);
    }

    [Theory]
    [InlineData(DataClass.Secret)]
    [InlineData(DataClass.Ephemeral)]
    public async Task Secret_and_ephemeral_never_leave_the_device_even_when_user_asks(DataClass cls)
    {
        var (r, or, jev, _, _) = Make();
        var d = await r.DecideAsync(Req(BrainTask.SummarizePage, cls, explicitAction: true), default);
        Assert.True(d.WasDenied);
        Assert.StartsWith("hard:", d.Rule);
        Assert.Empty(or.Received); Assert.Empty(jev.Received);
    }

    [Fact]
    public async Task Private_container_is_hard_denied_regardless_of_class()
    {
        var (r, or, _, _, _) = Make();
        var d = await r.DecideAsync(Req(BrainTask.SummarizePage, DataClass.Public, c: IdentityContainer.Private), default);
        Assert.True(d.WasDenied);
        Assert.Empty(or.Received);
    }

    [Fact]
    public async Task Public_page_explicit_request_goes_to_cloud_redacted()
    {
        var (r, or, _, _, _) = Make();
        or.Reply = "summary";
        var d = await r.DecideAsync(Req(BrainTask.SummarizePage, DataClass.Public), default);
        Assert.Equal("summary", d.Output);
        Assert.Equal(Provider.OpenRouter, d.Source);
        Assert.Equal("provider:openrouter:cloud", d.Rule);
        Assert.True(d.Redacted);
        Assert.Equal(2, d.RedactionCount);
        Assert.DoesNotContain("me@example.com", or.Received.Single());
        Assert.DoesNotContain("4111", or.Received.Single());
    }

    [Fact]
    public async Task Authenticated_page_needs_explicit_action_then_is_allowed_by_override()
    {
        var (r, or, _, _, _) = Make();
        var implicitReq = await r.DecideAsync(Req(BrainTask.SummarizePage, DataClass.Authenticated, explicitAction: false), default);
        Assert.True(implicitReq.WasDenied);
        Assert.Equal("policy:task_requires_explicit_user_action", implicitReq.Rule);
        var explicitReq = await r.DecideAsync(Req(BrainTask.SummarizePage, DataClass.Authenticated, explicitAction: true), default);
        Assert.Equal(Provider.OpenRouter, explicitReq.Source);
    }

    [Fact]
    public async Task Sensitive_page_is_local_only_no_provider_means_denied()
    {
        var (r, or, jev, _, _) = Make();
        var d = await r.DecideAsync(Req(BrainTask.SummarizePage, DataClass.Sensitive), default);
        Assert.True(d.WasDenied);
        Assert.Equal("policy:cloud_not_permitted_for_class", d.Rule);
        Assert.Empty(or.Received); Assert.Empty(jev.Received);
    }

    [Fact]
    public async Task Cloud_switch_off_keeps_ai_on_but_providers_untouched()
    {
        var (r, or, jev, _, _) = Make(cloud: false);
        var d = await r.DecideAsync(Req(BrainTask.SummarizePage, DataClass.Public), default);
        Assert.True(d.WasDenied);
        Assert.Empty(or.Received); Assert.Empty(jev.Received);
    }

    [Fact]
    public async Task Preference_order_and_fallback_when_unconfigured()
    {
        var or = new FakeProvider(Provider.OpenRouter, configured: false);
        var jev = new FakeProvider(Provider.Jev);
        var router = new BrainRouter(new DefaultTrustPolicy(), new BrainPolicy { AiEnabled = true, CloudEnabled = true }, [or, jev], clock: () => T0);
        var d = await router.DecideAsync(Req(BrainTask.SummarizePage, DataClass.Public), default);
        Assert.Equal(Provider.Jev, d.Source);
        Assert.Single(jev.Received);
    }

    [Fact]
    public async Task Schedule_hints_are_answered_by_rules_never_by_providers()
    {
        var (r, or, jev, _, _) = Make();
        var d = await r.DecideAsync(Req(BrainTask.ScheduleHint, DataClass.Public), default);
        Assert.Equal(Provider.Rules, d.Source);
        Assert.Empty(or.Received); Assert.Empty(jev.Received);
    }

    [Fact]
    public async Task Provider_failure_is_recorded_not_thrown()
    {
        var (r, or, _, log, _) = Make();
        or.Throw = true;
        var d = await r.DecideAsync(Req(BrainTask.SummarizePage, DataClass.Public), default);
        Assert.True(d.WasDenied);
        Assert.StartsWith("provider_failed:openrouter", d.Rule);
        Assert.Equal("OpenRouter-model", d.Model);
        Assert.Single(log);
    }

    [Fact]
    public async Task Oversized_input_is_refused_before_any_provider()
    {
        var (r, or, _, _, policy) = Make();
        policy.MaxInputChars = 10;
        var d = await r.DecideAsync(Req(BrainTask.SummarizePage, DataClass.Public), default);
        Assert.Equal("hard:input_too_large", d.Rule);
        Assert.Empty(or.Received);
    }
}

public class RedactorTests
{
    [Fact]
    public void Redacts_emails_cards_keys_and_phones()
    {
        var r = Redactor.Redact("mail me@x.org; card 4111 1111 1111 1111; key sk-abcdefghijklmnopqrstuvwxyz1234; call +91 98765 43210; token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop");
        Assert.DoesNotContain("me@x.org", r.Text);
        Assert.DoesNotContain("4111", r.Text);
        Assert.DoesNotContain("sk-abc", r.Text);
        Assert.DoesNotContain("98765", r.Text);
        Assert.DoesNotContain("eyJhbGci", r.Text);
        Assert.Contains("email", r.Kinds); Assert.Contains("card", r.Kinds); Assert.Contains("api_key", r.Kinds); Assert.Contains("phone", r.Kinds); Assert.Contains("jwt", r.Kinds);
    }

    [Fact]
    public void Non_luhn_digit_runs_are_left_alone()
    {
        var r = Redactor.Redact("order 1234 5678 9012 3456 shipped"); // fails Luhn
        Assert.Contains("1234 5678 9012 3456", r.Text);
    }

    [Fact]
    public void Plain_prose_is_untouched()
    {
        const string s = "WebView2 exposes request interception and native messaging. Version 2.5.1 was released in 2026.";
        var r = Redactor.Redact(s);
        Assert.Equal(s, r.Text);
        Assert.Equal(0, r.Count);
    }
}
