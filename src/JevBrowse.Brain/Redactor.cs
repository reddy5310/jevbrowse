using System.Text.RegularExpressions;

namespace JevBrowse.Brain;

/// <summary>
/// Scrubs obvious secrets before text leaves the machine (Table A.11 "AI receives secrets → redaction").
/// This is a backstop, not the primary control: Trust OS already refuses SECRET/SENSITIVE content entirely.
/// </summary>
public static partial class Redactor
{
    public sealed record Result(string Text, int Count, IReadOnlyList<string> Kinds);

    private static readonly (string Kind, Regex Rx)[] Patterns =
    [
        ("email", Email()),
        ("card", Card()),
        ("api_key", ApiKey()),
        ("jwt", Jwt()),
        ("iban", Iban()),
        ("phone", Phone()),
        ("aadhaar", Aadhaar()),
    ];

    public static Result Redact(string text)
    {
        int count = 0;
        var kinds = new List<string>();
        foreach (var (kind, rx) in Patterns)
        {
            text = rx.Replace(text, m =>
            {
                if (kind == "card" && !Luhn(m.Value)) return m.Value;
                count++;
                if (!kinds.Contains(kind)) kinds.Add(kind);
                return $"[REDACTED:{kind}]";
            });
        }
        return new Result(text, count, kinds);
    }

    private static bool Luhn(string s)
    {
        int sum = 0; bool alt = false;
        for (int i = s.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(s[i])) continue;
            int d = s[i] - '0';
            if (alt) { d *= 2; if (d > 9) d -= 9; }
            sum += d; alt = !alt;
        }
        return sum % 10 == 0;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")] private static partial Regex Email();
    [GeneratedRegex(@"\b(?:\d[ -]?){13,19}\b")] private static partial Regex Card();
    [GeneratedRegex(@"\b(?:sk|pk|rk|xoxb|xoxp|ghp|gho|AKIA|AIza)[A-Za-z0-9_\-]{16,}\b|\b[A-Fa-f0-9]{40,}\b")] private static partial Regex ApiKey();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b")] private static partial Regex Jwt();
    [GeneratedRegex(@"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b")] private static partial Regex Iban();
    // 10–13 digits with optional separators/+; lookarounds stop it biting into a longer (card-like) digit run.
    [GeneratedRegex(@"(?<!\d[ -]?)\+?(?:\d[ -]?){9,12}\d(?![ -]?\d)")] private static partial Regex Phone();
    [GeneratedRegex(@"(?<!\d[ -]?)\d{4}[ -]\d{4}[ -]\d{4}(?![ -]?\d)")] private static partial Regex Aadhaar();
}
