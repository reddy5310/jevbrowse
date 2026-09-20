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

/// <summary>
/// What an element IS, resolved in the page, so the gateway judges the real target (button text, label, form method)
/// rather than trusting the caller's selector string. Never contains input values.
/// </summary>
public sealed record ElementInfo(string Tag, string Type, string Text, string Label, string Name, string Href, string FormMethod)
{
    public string Describe() => $"{Tag} {Type} {Text} {Label} {Name} {Href}".Trim();
    public bool IsSubmit => Type.Equals("submit", StringComparison.OrdinalIgnoreCase) || (Tag.Equals("button", StringComparison.OrdinalIgnoreCase) && Type.Length == 0 && FormMethod.Length > 0);
}
