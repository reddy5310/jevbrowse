using JevBrowse.Domain;

namespace JevBrowse.TrustOS;

public sealed record ResourceContext(Uri Url, DataClass Class, IdentityContainer Container);

public interface ITrustPolicy
{
    TrustDecision Evaluate(ResourceContext resource, DataOperation operation);
}

/// <summary>
/// The persistence/AI matrix of Table A.10 and A.5, as code. Hard rule: rows only get stricter as the class rises,
/// and nothing below Trust OS (scheduler, brain, agents) can widen a decision made here.
/// </summary>
public sealed class DefaultTrustPolicy : ITrustPolicy
{
    public TrustDecision Evaluate(ResourceContext r, DataOperation op)
    {
        // Ephemeral containers: nothing durable, nothing shared. Checked first so no later rule can leak.
        if (r.Class == DataClass.Ephemeral || r.Container.IsEphemeral())
            return TrustDecision.Deny($"{r.Container} container is ephemeral");

        return (r.Class, op) switch
        {
            (_, DataOperation.PersistTabRow) => TrustDecision.Allow("URL/title are durable identity (Table A.5)"),

            (DataClass.Public, _) => TrustDecision.Allow("public page"),

            // UNKNOWN: we have no evidence either way, so keep it on this device. Local artifacts that help the user
            // (scroll, thumbnail) are allowed; building a searchable corpus or sending it anywhere is not.
            // Agents are still allowed: the user allow-listed the domain, and that grant is the control there.
            (DataClass.Unknown, DataOperation.PersistCheckpoint) => TrustDecision.Allow("scroll/favicon are low sensitivity"),
            (DataClass.Unknown, DataOperation.PersistThumbnail) => TrustDecision.Allow("local preview only"),
            (DataClass.Unknown, DataOperation.ExposeToAgent) => TrustDecision.Allow("the agent's domain grant is the control"),
            (DataClass.Unknown, DataOperation.IndexContent) => TrustDecision.Deny("not assessed: content is not indexed without positive evidence the page is public"),
            (DataClass.Unknown, DataOperation.SendToCloudAI) => TrustDecision.Deny("not assessed: nothing leaves the device"),

            (DataClass.Authenticated, DataOperation.PersistCheckpoint) => TrustDecision.Allow("scroll/favicon are low sensitivity"),
            (DataClass.Authenticated, DataOperation.PersistThumbnail) => TrustDecision.Allow("thumbnail is policy-controlled; allowed for authenticated"),
            (DataClass.Authenticated, DataOperation.IndexContent) => TrustDecision.Deny("content indexing off for authenticated pages unless user enables"),
            (DataClass.Authenticated, DataOperation.SendToCloudAI) => TrustDecision.Deny("cloud AI needs explicit user action on authenticated pages"),
            (DataClass.Authenticated, DataOperation.ExposeToAgent) => TrustDecision.Deny("agents need an explicit domain grant"),

            (DataClass.Sensitive, DataOperation.PersistCheckpoint) => TrustDecision.Allow("scroll only; no content"),
            (DataClass.Sensitive, _) => TrustDecision.Deny("sensitive class: minimal metadata only"),

            (DataClass.Secret, _) => TrustDecision.Deny("secret input present: never persisted by the custom state system"),

            _ => TrustDecision.Deny("no rule matched; default deny"),
        };
    }
}
