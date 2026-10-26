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
    string? Note)
{
    public static ParsedIngredientLine Of(string original, ParsedLineKind kind) =>
        new(original, kind, null, null, null, null, false, null, null);
}

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

    private static readonly string[] ToTastePhrases =
    [
        "etter smak", "til steking", "til servering", "til pensling", "til smøring", "til dekorasjon", "to taste",
        "for frying", "for serving", "for greasing", "for garnish", "as needed",
    ];

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

        var notes = new List<string>();
        text = ParenRegex().Replace(text, m =>
        {
            var inner = m.Groups[1].Value.Trim();
            if (inner.Length > 0)
            {
                notes.Add(inner);
            }

            return " ";
        });
        text = text.Replace("(", " ").Replace(")", " ");
        text = SpacesRegex().Replace(text, " ").Trim();

        var (main, tail) = SplitAtNoteComma(text);
        if (tail.Length > 0)
        {
            notes.Add(tail);
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

        rest = StripLeadingOf(rest);
        foreach (var phrase in ToTastePhrases)
        {
            var at = rest.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                notes.Add(phrase);
                rest = (rest[..at] + " " + rest[(at + phrase.Length)..]).Trim();
            }
        }

        rest = SpacesRegex().Replace(rest, " ").Trim().TrimEnd('.', ';', ':', '-').Trim();
        if (rest.Length == 0 || !rest.Any(char.IsLetter))
        {
            return ParsedIngredientLine.Of(original, ParsedLineKind.Unparseable);
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

        return new ParsedIngredientLine(original, ParsedLineKind.Parsed, quantity, low, unitCode, unitText, implied, rest, note);
    }

    /// <summary>Lower-cases nothing; fixes the characters: bullets, unicode fractions, dashes, odd spaces.</summary>
    private static string Preprocess(string input)
    {
        var text = input.Replace(' ', ' ').Replace(' ', ' ').Replace(' ', ' ').Replace('⁄', '/')
            .Replace('–', '-').Replace('—', '-').Replace('−', '-');
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
            || line.IngredientText is null || line.IngredientText.Any(char.IsDigit))
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
