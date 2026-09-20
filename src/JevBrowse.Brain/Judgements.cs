using JevBrowse.Domain;

namespace JevBrowse.Brain;

/// <summary>
/// The typed questions JevBrowse asks Jev. Kept in one place so the decision log, the docs and the tests agree on
/// exactly what leaves the machine: a short state string (title, host, headings, structural flags), never page content.
/// </summary>
public static class Judgements
{
    public const string DataClassQ = "data_class";
    public const string ClutterQ = "clutter";
    public const string RevisitQ = "revisit";

    public static IReadOnlyDictionary<string, Question> PageQuestions() => new Dictionary<string, Question>
    {
        [DataClassQ] = new ChoiceQuestion("Classify this web page's sensitivity for a privacy-first browser.", new Dictionary<string, string>
        {
            ["public"] = "Public article, documentation, product page or forum readable without an account",
            ["authenticated"] = "Behind a login: mail, chat, dashboards, account or settings pages, private repos",
            ["sensitive"] = "Banking, brokerage, payments, health records, payroll, government identity",
        }),
        [ClutterQ] = new NoulQuestion("The page is likely dominated by ads, recommendation feeds or engagement traps rather than the content the user came for."),
    };

    public static string PageState(Uri url, string title, IEnumerable<string> headings, PageSignals signals, int linkCount, int fieldCount) =>
        $"Host: {url.Host}\nPath: {url.AbsolutePath}\nTitle: {title}\nHeadings: {string.Join(" | ", headings.Take(8))}\n" +
        $"Password field: {(signals.HasFlag(PageSignals.PasswordField) ? "yes" : "no")}; payment field: {(signals.HasFlag(PageSignals.PaymentField) ? "yes" : "no")}; links: {linkCount}; form fields: {fieldCount}";

    /// <summary>Only raises: Jev may make a page more protected, never less (Constitution rule 9).</summary>
    public static DataClass? RaiseFrom(DataClass current, ChoiceAnswer answer, double minConfidence = 0.7)
    {
        if (answer.Confidence < minConfidence) return null;
        var suggested = answer.Choice switch { "authenticated" => DataClass.Authenticated, "sensitive" => DataClass.Sensitive, _ => DataClass.Public };
        return suggested > current ? suggested : null;
    }

    /// <summary>
    /// Semantic clutter pass (§9 "optional semantic clutter classifier"): one noul per residual empty box the
    /// deterministic collapse could not classify. Descriptors are structural (tag, id, class, size, position,
    /// neighbours), never page text beyond a short label.
    /// </summary>
    public static IReadOnlyDictionary<string, Question> ClutterQuestions(IReadOnlyList<string> elementDescriptors)
    {
        var d = new Dictionary<string, Question>();
        for (int i = 0; i < elementDescriptors.Count; i++)
            d[$"e{i}"] = new NoulQuestion($"Element {i + 1} in the state is an advertisement slot or sponsored placeholder (empty because its ad was blocked), rather than site content that is merely empty or still loading.");
        return d;
    }

    public const string WorkspaceQ = "workspace";

    /// <summary>
    /// Which of the user's workspaces this page belongs to. Option KEYS are opaque ("w0", "w1"): keys are not
    /// redacted, and a workspace can be named after a person or a plan. Names appear only in the descriptions, which
    /// the router redacts. Map the answer back with <see cref="WorkspaceNameFor"/>. The user confirms any move.
    /// </summary>
    public static IReadOnlyDictionary<string, Question> WorkspaceQuestion(IReadOnlyList<string> workspaceNames) => new Dictionary<string, Question>
    {
        [WorkspaceQ] = new ChoiceQuestion("Which of the user's workspaces does this page most plausibly belong to?",
            workspaceNames.Distinct().Select((n, i) => (Key: $"w{i}", Desc: $"The workspace named '{n}'")).ToDictionary(x => x.Key, x => x.Desc)),
    };

    public static string? WorkspaceNameFor(string choiceKey, IReadOnlyList<string> workspaceNames)
    {
        var names = workspaceNames.Distinct().ToList();
        return choiceKey.Length > 1 && choiceKey[0] == 'w' && int.TryParse(choiceKey.AsSpan(1), out var i) && i >= 0 && i < names.Count ? names[i] : null;
    }

    public static IReadOnlyDictionary<string, Question> RerankQuestions(IReadOnlyList<string> candidateTitles)
    {
        var d = new Dictionary<string, Question>();
        for (int i = 0; i < candidateTitles.Count; i++)
            d[$"c{i}"] = new ScoreQuestion($"How well does candidate {i + 1} (\"{candidateTitles[i]}\") answer the query in the state?", ["Irrelevant", "Partly relevant", "Directly answers it"]);
        return d;
    }
}
