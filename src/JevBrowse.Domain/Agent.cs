namespace JevBrowse.Domain;

/// <summary>Actions an agent may be granted (§12.1). There is deliberately no "evaluate script" or "raw DOM".</summary>
public enum AgentAction { Navigate, Read, Click, TypeNonSecret, Screenshot }

public sealed record PageField(string Name, string Type, string? Label);
public sealed record PageLink(string Text, string Href);

/// <summary>
/// Page Map (§12): the structured, reduced view an agent gets instead of the DOM. Field *values* are never included;
/// password fields appear only as a type so the agent knows not to ask for them.
/// </summary>
public sealed record PageMap(
    Uri Url,
    string Title,
    IReadOnlyList<string> Headings,
    IReadOnlyList<PageLink> Links,
    IReadOnlyList<PageField> Fields,
    string TextExcerpt);

public sealed record ActionResult(bool Ok, string Message);
