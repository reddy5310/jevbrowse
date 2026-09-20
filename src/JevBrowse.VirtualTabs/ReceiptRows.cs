namespace JevBrowse.VirtualTabs;

/// <summary>Everything the Receipt reports, as plain facts. No formatting, no UI types.</summary>
public sealed record ReceiptFacts(
    string Host, TimeSpan OpenFor, int ShieldChecked, int ThirdParty, int ThirdPartyHosts, int Blocked,
    double GroupMemoryMb, int AwakeTabs, string TreatmentLabel, string TreatmentMeaning, string Profile,
    IReadOnlyList<string> StayingAwakeBecause);

/// <summary>
/// The Receipt as label/value pairs in ordinary words. It used to be a monospace table that wrapped badly and spoke in
/// implementation terms ("measured since renderer attached", "attributed memory … equal share … across live renderers",
/// "data class Unknown"). A receipt is supposed to answer "what did this site do?", so it should say that.
/// </summary>
public static class ReceiptRows
{
    public static string Title(ReceiptFacts f) => $"What {f.Host} did during your visit";

    public static IReadOnlyList<(string Label, string Value)> Build(ReceiptFacts f)
    {
        static string N(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";
        var perTab = f.AwakeTabs <= 0 ? 0 : f.GroupMemoryMb / f.AwakeTabs;

        return
        [
            ("Open for", f.OpenFor.TotalHours >= 1 ? f.OpenFor.ToString(@"h\:mm\:ss") : f.OpenFor.ToString(@"m\:ss")),
            // Shield only counts what it inspected, so that is what this says; it is not a claim about every request
            // the page made.
            ("Checked by Shield", N(f.ShieldChecked, "request", "requests")),
            ("Sent to other sites", f.ThirdParty == 0 ? "nothing" : $"{N(f.ThirdParty, "request", "requests")} to {N(f.ThirdPartyHosts, "other site", "other sites")}"),
            ("Blocked", f.Blocked == 0 ? "nothing" : N(f.Blocked, "request", "requests")),
            ("Memory", $"about {perTab:F0} MB, an estimate: the {f.GroupMemoryMb:F0} MB used by all pages, shared equally between {N(f.AwakeTabs, "awake tab", "awake tabs")}"),
            ("How JevBrowse treats it", $"{f.TreatmentLabel}. {f.TreatmentMeaning}"),
            ("Profile", f.Profile),
            ("Kept awake because", f.StayingAwakeBecause.Count == 0 ? "nothing: it can sleep when memory is needed" : string.Join(", ", f.StayingAwakeBecause)),
        ];
    }

    public const string Footnote = "Numbers described as “about” are estimates. How much data the page transferred is not measured yet.";
}
