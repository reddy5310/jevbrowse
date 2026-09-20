using JevBrowse.Domain;

namespace JevBrowse.Brain;

/// <summary>What the browser might ask a brain to do. Each task has a default provider ceiling (Table A.8).</summary>
public enum BrainTask
{
    ScheduleHint,     // "should this tab be virtualized?" — rules/local only; Jev optional; never LLM
    ClassifyPage,     // ambiguous data class / clutter — Jev default
    SummarizePage,    // explicit user request — OpenRouter/Jev/local
    ExplainError,     // DevSpace — explicit user request
    RerankSearch,     // Browser Memory — Jev optional
}

public enum Provider { None, Rules, Local, Jev, OpenRouter, Custom }

public sealed record DecisionRequest(
    BrainTask Task,
    string Input,
    DataClass DataClass,
    IdentityContainer Container,
    bool ExplicitUserAction,
    Uri? Url = null);

/// <summary>Decision record (§11.1): source, rule, provider and version are always present and cannot be forged by a provider.</summary>
public sealed record Decision(
    string Output,
    Provider Source,
    string Rule,
    string? Model,
    bool Redacted,
    int RedactionCount,
    int InputChars,
    DateTimeOffset At,
    string Version = "brain/1",
    string Task = "",
    string DataClassName = "",
    bool Automatic = false)
{
    public bool WasDenied => Source == Provider.None;
}

public interface IBrainRouter
{
    Task<Decision> DecideAsync(DecisionRequest request, CancellationToken ct);
}

/// <summary>A model endpoint. Providers are dumb: they never see Trust OS, never decide, and only receive redacted text.</summary>
public interface IAiProvider
{
    Provider Kind { get; }
    string Model { get; }
    bool IsConfigured { get; }
    Task<string> CompleteAsync(string system, string user, CancellationToken ct);
}
