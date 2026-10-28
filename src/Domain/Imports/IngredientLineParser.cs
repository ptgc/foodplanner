using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Imports;

public enum ParsedLineKind
{
    /// <summary>An ingredient was found (possibly without quantity: "to taste").</summary>
    Parsed,

    /// <summary>Nothing usable (empty, no letters, a quantity and a unit only). Kept as an unmatched line.</summary>
    Unparseable,

    /// <summary>A section label such as "Til sausen:". Not an ingredient; callers skip it.</summary>
    Heading,
}

/// <summary>
/// One recipe line split into parts. <see cref="Quantity"/> null with no unit means "to taste". For a range
/// ("2-3 fedd") <see cref="Quantity"/> is the upper bound (enough to buy) and <see cref="QuantityLow"/> the lower. A
/// quantity without a unit word gets the unit <c>stk</c> with <see cref="UnitImplied"/> true ("3 egg"). <see cref="UnitText"/>
/// is a unit word that the catalog does not have ("boks", "oz"): the line then has no <see cref="UnitCode"/>.
/// <see cref="Original"/> is the input as given; it is kept only in the import artifact (CLAUDE.md rule 5).
/// </summary>
public sealed record ParsedIngredientLine(
    string Original,
    ParsedLineKind Kind,
    decimal? Quantity,
    decimal? QuantityLow,
    string? UnitCode,
    string? UnitText,
    bool UnitImplied,
    string? IngredientText,
    string? Note,
    AmountKind AmountKind,
    IngredientPurpose Purpose,
    bool IsOptional,
    IReadOnlyList<ParsedAlternative> Alternatives,
    string? UnsplitText)
{
    public static ParsedIngredientLine Of(string original, ParsedLineKind kind) =>
        new(original, kind, null, null, null, null, false, null, null, AmountKind.ToTaste, IngredientPurpose.Main, false, [], null);
}

/// <summary>
/// An "or X" of an ingredient line (T-076): <see cref="Text"/> is the ingredient to match against the catalog,
/// <see cref="NoteText"/> how it reads in the note when it does not match ("eller olje"; unmatched alternatives become note
/// text, never free-text entities).
/// </summary>
public sealed record ParsedAlternative(string Text, string NoteText);

/// <summary>
/// Rule-based parser for Norwegian and English ingredient lines: "2 dl melk", "1/2 ts salt", "1½ cup flour", "3 egg",
/// "200 g smør, myk", "2-3 fedd hvitløk", "salt og pepper". Pure and deterministic; it never guesses an ingredient id (that
/// is <see cref="IngredientMatcher"/>). A line it cannot read is returned as <see cref="ParsedLineKind.Unparseable"/>.
/// </summary>
public static partial class IngredientLineParser
{
    private const string Num = @"\d+/\d+|\d+(?:[.,]\d+)?(?:\s+\d+/\d+)?";

    private static readonly Dictionary<char, string> UnicodeFractions = new()
    {
        ['½'] = "1/2",
        ['⅓'] = "1/3",
        ['⅔'] = "2/3",
        ['¼'] = "1/4",
        ['¾'] = "3/4",
        ['⅕'] = "1/5",
        ['⅖'] = "2/5",
        ['⅗'] = "3/5",
        ['⅘'] = "4/5",
        ['⅙'] = "1/6",
        ['⅚'] = "5/6",
        ['⅐'] = "1/7",
        ['⅛'] = "1/8",
        ['⅜'] = "3/8",
        ['⅝'] = "5/8",
        ['⅞'] = "7/8",
        ['⅑'] = "1/9",
        ['⅒'] = "1/10",
    };

    // Phrases that say what a line is for (T-076). Each is matched as whole words, case-insensitively, and removed from the
    // text when it is fully represented by the structured fields; "til pensling" style phrases set AsNeeded but stay in the note.
    private const string WordStart = @"(?<![\p{L}\p{N}])";
    private const string WordEnd = @"(?![\p{L}\p{N}])";
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(WordStart + @"(?:etter\s+smak|to\s+taste)" + WordEnd, Ci)]
    private static partial Regex ToTasteRegex();

    [GeneratedRegex(WordStart + @"(?:til\s+(?:å\s+)?(?:steking|stekning|steiking|steke(?:\s+i)?)|å\s+steke\s+i|for\s+(?:pan[\s-]?)?frying|to\s+fry\s+(?:with|in))" + WordEnd, Ci)]
    private static partial Regex FryingRegex();

    [GeneratedRegex(WordStart + @"(?:til\s+(?:å\s+)?servering|til\s+å\s+servere|for\s+serving|to\s+serve(?:\s+with)?)" + WordEnd, Ci)]
    private static partial Regex ServingRegex();

    [GeneratedRegex(WordStart + @"(?:til\s+pynt(?:ing)?|til\s+dekorasjon|til\s+garnityr|(?:for|to|as)\s+garnish(?:ing)?)" + WordEnd, Ci)]
    private static partial Regex GarnishRegex();

    [GeneratedRegex(WordStart + @"(?:etter\s+behov|ved\s+behov|as\s+needed|as\s+required)" + WordEnd, Ci)]
    private static partial Regex AsNeededRegex();

    [GeneratedRegex(WordStart + @"(?:til\s+pensling|til\s+smøring|til\s+å\s+(?:smøre|pensle)|for\s+(?:greasing|brushing))" + WordEnd, Ci)]
    private static partial Regex AsNeededKeptRegex();

    [GeneratedRegex(WordStart + @"(?:kan\s+(?:sløyfes|utelates|droppes|sløyfe)|valgfri(?:tt)?|optional|if\s+desired|if\s+you\s+like|can\s+be\s+(?:left\s+out|omitted)|may\s+be\s+omitted)" + WordEnd, Ci)]
    private static partial Regex OptionalRegex();

    [GeneratedRegex(@"^(?:evt\.?|ev\.|eventuelt)\s+(?=\S)", Ci)]
    private static partial Regex LeadingOptionalRegex();

    [GeneratedRegex(@"^(?:evt|ev|eventuelt)\.?$", Ci)]
    private static partial Regex LoneOptionalRegex();

    [GeneratedRegex(@"^(?<m>evt\.?|ev\.|eventuelt|eller|or)\s+(?<alt>.+)$", Ci)]
    private static partial Regex LeadingAlternativeRegex();

    [GeneratedRegex(@"\s+(?<m>eller|or|evt\.?|eventuelt)\s+", Ci)]
    private static partial Regex AlternativeSplitRegex();

    [GeneratedRegex(@"^(?:annen|annet|andre|other|another|any\s+other)\b", Ci)]
    private static partial Regex GenericAlternativeRegex();

    private const int MaxAlternatives = RecipeIngredientAlternative.MaxPerLine;
    private const int MaxAlternativeWords = 5;

    /// <summary>What the phrases of one line said while it was being read.</summary>
    private sealed class Flags
    {
        public bool ToTaste;
        public bool AsNeeded;
        public bool Optional;
        public bool Frying;
        public bool Serving;
        public bool Garnish;
        public List<ParsedAlternative> Alternatives { get; } = [];
        public List<string> Notes { get; } = [];
    }

    [GeneratedRegex(@"^(?<a>" + Num + @")(?:(?:\s*-\s*|\s+(?:to|til|or|eller)\s+)(?<b>" + Num + @"))?", RegexOptions.CultureInvariant)]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"^(?:(?:en|ei|et|ett|one|a|an)\s+)?(?:halv|halvt|halve|half)\s+(?:(?:a|an)\s+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HalfRegex();

    [GeneratedRegex(@"^(?<w>en|ei|et|ett|one|a|an)\s+(?!(?:little|bit|few|touch|splash|litt|lite|noen)\b)(?=\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OneRegex();

    [GeneratedRegex(@"^\p{L}+\.?", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"^(?:ca\.?|circa|cirka|approx\.?|about|~)\s*(?=\d|en\b|ei\b|et\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApproxRegex();

    [GeneratedRegex(@"\(([^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ParenRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"(\d)\s*([½⅓⅔¼¾⅕⅖⅗⅘⅙⅚⅐⅛⅜⅝⅞⅑⅒])", RegexOptions.CultureInvariant)]
    private static partial Regex MixedUnicodeFractionRegex();

    [GeneratedRegex(@"\s+(?:og|and|&)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AndRegex();

    /// <summary>
    /// Parses every line and splits "salt og pepper" (no quantity, short parts on both sides) into one line per
    /// ingredient. Headings are dropped. Unparseable lines are kept, in order.
    /// </summary>
    public static IReadOnlyList<ParsedIngredientLine> ParseAll(IEnumerable<string?> lines)
    {
        var result = new List<ParsedIngredientLine>();
        foreach (var line in lines)
        {
            var parsed = Parse(line);
            if (parsed.Kind == ParsedLineKind.Heading)
            {
                continue;
            }

            result.AddRange(SplitAnd(parsed));
        }

        return result;
    }

    public static ParsedIngredientLine Parse(string? line)
    {
        var original = line ?? string.Empty;
        var text = Preprocess(original);
        if (text.Length == 0 || !text.Any(char.IsLetter))
        {
            return ParsedIngredientLine.Of(original, ParsedLineKind.Unparseable);
        }

        if (text.EndsWith(':') && !text.Any(char.IsDigit))
        {
            return ParsedIngredientLine.Of(original, ParsedLineKind.Heading);
        }

        var flags = new Flags();
        var notes = flags.Notes;
        var fragments = new List<string>();
        text = ParenRegex().Replace(text, m =>
        {
            var inner = m.Groups[1].Value.Trim();
            if (inner.Length > 0)
            {
                fragments.Add(inner);
            }

            return " ";
        });
        text = text.Replace("(", " ").Replace(")", " ");
        text = SpacesRegex().Replace(text, " ").Trim();

        var (main, tail) = SplitAtNoteComma(text);
        if (LeadingOptionalRegex().Match(main) is { Success: true } leadingOptional)
        {
            flags.Optional = true;
            main = main[leadingOptional.Length..].TrimStart();
        }

        decimal? quantity = null;
        decimal? low = null;
        string? unitCode = null;
        string? unitText = null;
        var implied = false;
        var rest = main;

        var half = HalfRegex().Match(rest);
        var quantityMatch = QuantityRegex().Match(rest);
        if (half.Success)
        {
            quantity = 0.5m;
            rest = rest[half.Length..].Trim();
        }
        else if (quantityMatch.Success)
        {
            var upper = Evaluate(quantityMatch.Groups["b"].Success ? quantityMatch.Groups["b"].Value : quantityMatch.Groups["a"].Value);
            if (quantityMatch.Groups["b"].Success)
            {
                var lower = Evaluate(quantityMatch.Groups["a"].Value);
                if (lower is null || upper is null)
                {
                    return ParsedIngredientLine.Of(original, ParsedLineKind.Unparseable);
                }

                (low, quantity) = lower <= upper ? (lower, upper) : (upper, lower);
                if (low == quantity)
                {
                    low = null;
                }
            }
            else
            {
                quantity = upper;
            }

            if (quantity is null || quantity <= 0 || quantity > RecipeIngredient.MaxQuantity)
            {
                return ParsedIngredientLine.Of(original, ParsedLineKind.Unparseable);
            }

            rest = rest[quantityMatch.Length..].Trim();
        }
        else if (OneRegex().Match(rest) is { Success: true } one)
        {
            quantity = 1m;
            rest = rest[one.Length..].Trim();
        }

        if (quantity is not null)
        {
            var word = WordRegex().Match(rest);
            if (word.Success && UnitWords.CodeFor(word.Value) is { } code)
            {
                unitCode = code;
                rest = rest[word.Length..].Trim();
            }
            else if (word.Success && UnitWords.IsUnmapped(word.Value))
            {
                unitText = word.Value.TrimEnd('.').ToLowerInvariant();
                rest = rest[word.Length..].Trim();
            }
            else
            {
                unitCode = "stk";
                implied = true;
            }
        }

        foreach (var fragment in fragments)
        {
            Absorb(fragment, flags);
        }

        rest = Clean(Consume(StripLeadingOf(rest), flags));
        if (rest.Length == 0 || !rest.Any(char.IsLetter))
        {
            return ParsedIngredientLine.Of(original, ParsedLineKind.Unparseable);
        }

        // "margarin eller olje": the first is the ingredient, the others are alternatives (T-076).
        string? unsplit = null;
        var splitRest = SplitAlternatives(rest);
        if (splitRest is not null)
        {
            unsplit = rest;
            rest = splitRest[0].Text;
            flags.Alternatives.AddRange(splitRest.Skip(1).Select(p => new ParsedAlternative(p.Text, $"{p.Marker} {p.Text}")));
        }

        if (tail.Length > 0)
        {
            var consumedTail = Consume(tail, flags);
            if (!CompleteGenericAlternative(consumedTail, flags))
            {
                Absorb(consumedTail, flags);
            }
        }

        if (low is { } lowValue && quantity is { } high)
        {
            notes.Insert(0, string.Create(CultureInfo.InvariantCulture, $"{lowValue:0.####}-{high:0.####}"));
        }

        var note = notes.Count == 0 ? null : string.Join("; ", notes);
        if (note is { Length: > RecipeIngredient.MaxNoteLength })
        {
            note = note[..RecipeIngredient.MaxNoteLength].TrimEnd();
        }

        var purpose = flags.Frying ? IngredientPurpose.Frying
            : flags.Serving ? IngredientPurpose.Serving
            : flags.Garnish ? IngredientPurpose.Garnish
            : flags.ToTaste ? IngredientPurpose.Seasoning
            : IngredientPurpose.Main;
        AmountKind kind;
        if (quantity is not null)
        {
            kind = AmountKind.Measured;
        }
        else if (flags.ToTaste)
        {
            kind = AmountKind.ToTaste;
        }
        else if (flags.AsNeeded || purpose is IngredientPurpose.Frying or IngredientPurpose.Serving or IngredientPurpose.Garnish)
        {
            kind = AmountKind.AsNeeded;
        }
        else
        {
            // No amount and no phrase ("salt og pepper"): to taste.
            kind = AmountKind.ToTaste;
            if (purpose == IngredientPurpose.Main)
            {
                purpose = IngredientPurpose.Seasoning;
            }
        }

        return new ParsedIngredientLine(
            original, ParsedLineKind.Parsed, quantity, low, unitCode, unitText, implied, rest, note,
            kind, purpose, flags.Optional, flags.Alternatives, unsplit);
    }

    /// <summary>
    /// Finds the phrases that say what a line is for ("etter smak", "til steking", "til servering", "til pynt", "kan sløyfes")
    /// and records them in <paramref name="flags"/>. The phrases the structured fields fully represent are removed from the
    /// text; the "til pensling" kind sets AsNeeded and stays.
    /// </summary>
    private static string Consume(string text, Flags flags)
    {
        text = Remove(ToTasteRegex(), text, () => flags.ToTaste = true);
        text = Remove(FryingRegex(), text, () => flags.Frying = true);
        text = Remove(ServingRegex(), text, () => flags.Serving = true);
        text = Remove(GarnishRegex(), text, () => flags.Garnish = true);
        text = Remove(AsNeededRegex(), text, () => flags.AsNeeded = true);
        text = Remove(OptionalRegex(), text, () => flags.Optional = true);
        text = AsNeededKeptRegex().Replace(text, m =>
        {
            flags.AsNeeded = true;
            flags.Notes.Add(m.Value);
            return " ";
        });

        return Clean(text);
    }

    private static string Remove(Regex regex, string text, Action found)
    {
        var any = false;
        var result = regex.Replace(text, _ =>
        {
            any = true;
            return " ";
        });
        if (any)
        {
            found();
        }

        return result;
    }

    private static string Clean(string text) =>
        SpacesRegex().Replace(text, " ").Trim().Trim(',', ';', ':', '-', '.', ' ').Trim();

    /// <summary>
    /// A note fragment (the inside of parentheses, or the text after the first comma). "evt." alone means optional; "evt. X",
    /// "eller X" and "or X" are alternatives (also kept as note text if X does not match an ingredient); anything else is a note.
    /// </summary>
    private static void Absorb(string fragment, Flags flags)
    {
        var text = Consume(fragment, flags);
        if (text.Length == 0)
        {
            return;
        }

        if (LoneOptionalRegex().IsMatch(text))
        {
            flags.Optional = true;
            return;
        }

        if (LeadingAlternativeRegex().Match(text) is { Success: true } lead)
        {
            var parts = SplitAlternatives(lead.Groups["alt"].Value, lead.Groups["m"].Value);
            if (parts is not null)
            {
                flags.Alternatives.AddRange(parts.Select(p => new ParsedAlternative(p.Text, $"{p.Marker} {p.Text}")));
                return;
            }
        }

        flags.Notes.Add(text);
    }

    /// <summary>
    /// "torskefilet eller annen kokt, hvit fisk": a generic last alternative ("annen ...") continues after the comma. The
    /// tail then belongs to that alternative instead of the note.
    /// </summary>
    private static bool CompleteGenericAlternative(string tail, Flags flags)
    {
        if (flags.Alternatives.Count == 0 || tail.Length == 0 || tail.Any(char.IsDigit) || tail.Split(' ').Length > 3)
        {
            return false;
        }

        var last = flags.Alternatives[^1];
        if (!GenericAlternativeRegex().IsMatch(last.Text) || LeadingAlternativeRegex().IsMatch(tail))
        {
            return false;
        }

        flags.Alternatives[^1] = new ParsedAlternative($"{last.Text} {tail}", $"{last.NoteText}, {tail}");
        return true;
    }

    /// <summary>
    /// Splits "a eller b eller c" at the markers; null when it is not a plain list of alternatives (a part has a digit or
    /// is long, there are too many). The first part is the primary, unless <paramref name="firstMarker"/> is given, which
    /// then precedes the first part (all parts are alternatives).
    /// </summary>
    private static List<(string Text, string Marker)>? SplitAlternatives(string text, string? firstMarker = null)
    {
        var pieces = AlternativeSplitRegex().Split(text);

        // Split with a capture group yields: text, marker, text, marker, text ...
        if (pieces.Length == 1 && firstMarker is null)
        {
            return null;
        }

        var result = new List<(string, string)>();
        for (var i = 0; i < pieces.Length; i += 2)
        {
            var part = Clean(pieces[i]);
            var marker = i == 0 ? firstMarker ?? string.Empty : pieces[i - 1];
            if (part.Length == 0 || !part.Any(char.IsLetter) || part.Any(char.IsDigit) || part.Split(' ').Length > MaxAlternativeWords)
            {
                return null;
            }

            result.Add((part, marker.Trim()));
        }

        var alternatives = firstMarker is null ? result.Count - 1 : result.Count;
        return alternatives is < 1 or > MaxAlternatives ? null : result;
    }

    /// <summary>Lower-cases nothing; fixes the characters: bullets, unicode fractions, dashes, odd spaces.</summary>
    private static string Preprocess(string input)
    {
        // Escaped by code point on purpose: no-break space, thin space, narrow no-break space, fraction slash, en dash, em dash, minus sign.
        var text = input.Replace((char)0x00A0, ' ').Replace((char)0x2009, ' ').Replace((char)0x202F, ' ').Replace((char)0x2044, '/')
            .Replace((char)0x2013, '-').Replace((char)0x2014, '-').Replace((char)0x2212, '-');
        text = MixedUnicodeFractionRegex().Replace(text, "$1 $2");
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (UnicodeFractions.TryGetValue(c, out var fraction))
            {
                sb.Append(fraction);
            }
            else if (!char.IsControl(c) || char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }

        text = SpacesRegex().Replace(sb.ToString(), " ").Trim();
        text = text.TrimStart('-', '•', '*', '·', '▪', '●', '■', '□', '☐', '✓', '○').Trim();
        return ApproxRegex().Replace(text, string.Empty).Trim();
    }

    /// <summary>The first comma that is not a decimal comma ("2,5") ends the ingredient; the rest is a note.</summary>
    private static (string Main, string Tail) SplitAtNoteComma(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != ',')
            {
                continue;
            }

            var decimalComma = i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]);
            if (!decimalComma)
            {
                return (text[..i].Trim(), text[(i + 1)..].Trim().TrimEnd('.'));
            }
        }

        return (text, string.Empty);
    }

    private static string StripLeadingOf(string text)
    {
        foreach (var prefix in new[] { "of ", "av " })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return text[prefix.Length..].TrimStart();
            }
        }

        return text;
    }

    /// <summary>"2", "2,5", "2.5", "1/2", "1 1/2" to a decimal with at most 4 decimals; null when invalid (zero denominator).</summary>
    private static decimal? Evaluate(string number)
    {
        var parts = number.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        decimal total = 0;
        foreach (var part in parts)
        {
            if (part.Contains('/'))
            {
                var pieces = part.Split('/');
                if (!decimal.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var numerator)
                    || !decimal.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator)
                    || denominator == 0)
                {
                    return null;
                }

                total += numerator / denominator;
            }
            else if (decimal.TryParse(part.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            {
                total += value;
            }
            else
            {
                return null;
            }
        }

        return decimal.Round(total, RecipeIngredient.MaxQuantityDecimals, MidpointRounding.AwayFromZero);
    }

    private static IEnumerable<ParsedIngredientLine> SplitAnd(ParsedIngredientLine line)
    {
        if (line.Kind != ParsedLineKind.Parsed || line.Quantity is not null || line.UnitText is not null || line.Note is not null
            || line.Alternatives.Count > 0 || line.IngredientText is null || line.IngredientText.Any(char.IsDigit))
        {
            yield return line;
            yield break;
        }

        var parts = AndRegex().Split(line.IngredientText);
        if (parts.Length < 2 || parts.Length > 3 || parts.Any(p => p.Trim().Length == 0 || p.Trim().Split(' ').Length > 4))
        {
            yield return line;
            yield break;
        }

        foreach (var part in parts)
        {
            yield return line with { IngredientText = part.Trim() };
        }
    }
}
