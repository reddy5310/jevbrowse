namespace JevBrowse.Shield;

[Flags]
public enum TypeMask
{
    None = 0,
    Document = 1 << RequestType.Document, SubDocument = 1 << RequestType.SubDocument, Script = 1 << RequestType.Script,
    Stylesheet = 1 << RequestType.Stylesheet, Image = 1 << RequestType.Image, Font = 1 << RequestType.Font, Media = 1 << RequestType.Media,
    XmlHttpRequest = 1 << RequestType.XmlHttpRequest, WebSocket = 1 << RequestType.WebSocket, Ping = 1 << RequestType.Ping, Other = 1 << RequestType.Other,
    All = (1 << 11) - 1,
}

public enum Anchor { None, Domain, Start, End, StartAndEnd }

/// <summary>
/// One compiled network rule. Supported subset of the Adblock Plus / uBlock syntax used by EasyList and EasyPrivacy:
/// `||host^`, `|start`, `end|`, `*` wildcards, `^` separators, `@@` exceptions, and the `$` options
/// third-party / ~third-party / domain= / resource types. Cosmetic (`##`) and regex rules are parsed elsewhere or skipped.
/// </summary>
public sealed class FilterRule
{
    public required string Raw { get; init; }
    public required string Pattern { get; init; }       // lowercase, with `^` and `*` kept as-is
    public required Anchor Anchor { get; init; }
    public required bool IsException { get; init; }
    public bool? ThirdParty { get; init; }               // null = don't care
    public TypeMask Types { get; init; } = TypeMask.All;
    public string[] DomainsInclude { get; init; } = [];
    public string[] DomainsExclude { get; init; } = [];
    /// <summary>Longest literal token (no wildcard/separator) in the pattern, used for indexing.</summary>
    public required string Token { get; init; }

    public static bool TryParse(string line, out FilterRule? rule)
    {
        rule = null;
        line = line.Trim();
        if (line.Length == 0 || line[0] == '!' || line[0] == '[' ) return false;
        if (line.Contains("##") || line.Contains("#@#") || line.Contains("#?#") || line.Contains("#$#")) return false; // cosmetic
        bool exception = line.StartsWith("@@");
        if (exception) line = line[2..];
        if (line.StartsWith('/') && line.EndsWith('/') && line.Length > 2) return false; // regex: unsupported in V1

        string pattern = line, options = "";
        int dollar = line.LastIndexOf('$');
        if (dollar > 0) { pattern = line[..dollar]; options = line[(dollar + 1)..]; }

        var anchor = Anchor.None;
        if (pattern.StartsWith("||")) { anchor = Anchor.Domain; pattern = pattern[2..]; }
        else if (pattern.StartsWith('|')) { anchor = Anchor.Start; pattern = pattern[1..]; }
        bool endAnchor = pattern.EndsWith('|');
        if (endAnchor) pattern = pattern[..^1];
        if (endAnchor) anchor = anchor == Anchor.Start ? Anchor.StartAndEnd : anchor == Anchor.None ? Anchor.End : anchor;
        pattern = pattern.ToLowerInvariant();
        if (pattern.Length == 0) return false;

        bool? thirdParty = null; var types = TypeMask.None; var negTypes = TypeMask.None;
        var inc = new List<string>(); var exc = new List<string>();
        foreach (var raw in options.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var opt = raw.Trim();
            bool neg = opt.StartsWith('~'); if (neg) opt = opt[1..];
            if (opt is "third-party" or "3p") thirdParty = !neg;
            else if (opt is "first-party" or "1p") thirdParty = neg;
            else if (opt.StartsWith("domain=", StringComparison.Ordinal))
                foreach (var d in opt[7..].Split('|', StringSplitOptions.RemoveEmptyEntries))
                    if (d.StartsWith('~')) exc.Add(d[1..].ToLowerInvariant()); else inc.Add(d.ToLowerInvariant());
            else if (TypeOf(opt) is { } t) { if (neg) negTypes |= t; else types |= t; }
            else if (opt is "important" or "match-case" or "badfilter" or "popup" or "generichide" or "genericblock" or "elemhide" or "document" && exception) { }
            else return false; // unknown option: skip rule rather than mis-apply it (conservative, §9)
        }
        if (types == TypeMask.None) types = TypeMask.All;
        if (negTypes != TypeMask.None) types &= ~negTypes;

        rule = new FilterRule
        {
            Raw = line, Pattern = pattern, Anchor = anchor, IsException = exception, ThirdParty = thirdParty,
            Types = types, DomainsInclude = [.. inc], DomainsExclude = [.. exc], Token = LongestToken(pattern),
        };
        return true;
    }

    private static TypeMask? TypeOf(string opt) => opt switch
    {
        "script" => TypeMask.Script, "image" => TypeMask.Image, "stylesheet" => TypeMask.Stylesheet, "font" => TypeMask.Font,
        "media" => TypeMask.Media, "xmlhttprequest" or "xhr" => TypeMask.XmlHttpRequest, "websocket" => TypeMask.WebSocket,
        "subdocument" or "frame" => TypeMask.SubDocument, "ping" or "beacon" => TypeMask.Ping, "other" => TypeMask.Other,
        "object" => TypeMask.Other, "document" or "doc" => TypeMask.Document,
        _ => null,
    };

    /// <summary>Tokens are runs of [a-z0-9-_.] so they line up with what the engine enumerates from URLs.</summary>
    private static string LongestToken(string pattern)
    {
        string best = "";
        int i = 0;
        while (i < pattern.Length)
        {
            if (!IsTokenChar(pattern[i])) { i++; continue; }
            int j = i;
            while (j < pattern.Length && IsTokenChar(pattern[j])) j++;
            if (j - i > best.Length) best = pattern[i..j];
            i = j;
        }
        return best;
    }

    internal static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';
}
