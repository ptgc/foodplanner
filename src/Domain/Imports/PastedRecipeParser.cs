using System.Globalization;
using System.Text.RegularExpressions;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Imports;

/// <summary>
/// Reads pasted recipe text (Norwegian or English) into title, servings, times, ingredient lines and steps with simple
/// heuristics. With section headings ("Ingredienser", "Fremgangsmåte", "Ingredients", "Method", ...) the text between them
/// is taken as is. Without headings the first line is the title, then lines that look like ingredients (short, starting
/// with an amount) are ingredients and everything from the first sentence on is steps.
/// </summary>
public static partial class PastedRecipeParser
{
    private enum Section
    {
        Intro,
        Ingredients,
        Steps,
    }

    [GeneratedRegex(@"^(ingredienser|ingredients|fremgangsmåte|framgangsmåte|fremgangsmate|slik gjør du( det)?|tilberedning|tilberedelse|metode|instruksjoner|instructions|directions|method|steps|preparation|how to make it)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(?:antall\s+)?(?:porsjoner|porsjon|servings?|serves|yield|makes|gir|ant\.?\s*personer|personer|til)\b\s*[:\-]?\s*(?<n>\d{1,3})\b(?:\s*(?:-|to|til)\s*(?<m>\d{1,3}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServingsPrefixRegex();

    [GeneratedRegex(@"^(?<n>\d{1,3})\s*(?:porsjoner|porsjon|personer|servings?|portions?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServingsSuffixRegex();

    [GeneratedRegex(@"^(?:forberedelsestid|forberedelse|prep(?:aration)?(?:\s*time)?)\b\s*[:\-]?\s*(?<t>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrepRegex();

    [GeneratedRegex(@"^(?:koketid|steketid|ovnstid|tilberedningstid|cook(?:ing)?(?:\s*time)?|bake\s*time|total(?:tid)?(?:\s*time)?|tid|time)\b\s*[:\-]?\s*(?<t>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CookRegex();

    [GeneratedRegex(@"^(?:(?:steg|step|trinn)\s*)?\d{1,2}\s*[.):]\s+\S", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedStepRegex();

    public static ImportedRecipe Parse(string? text)
    {
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Trim()).ToList();
        var warnings = new List<string>();
        string? title = null;
        int? servings = null;
        int? prep = null;
        int? cook = null;
        var intro = new List<string>();
        var ingredients = new List<string>();
        var steps = new List<string>();

        var hasHeadings = lines.Any(l => HeadingRegex().IsMatch(HeadingText(l)));
        var section = Section.Intro;
        var seenIngredient = false;

        foreach (var raw in lines)
        {
            if (raw.Length == 0)
            {
                continue;
            }

            var heading = HeadingText(raw);
            if (HeadingRegex().IsMatch(heading))
            {
                section = heading.StartsWith("ingredi", StringComparison.OrdinalIgnoreCase) ? Section.Ingredients : Section.Steps;
                continue;
            }

            if (TryMeta(raw, ref servings, ref prep, ref cook))
            {
                continue;
            }

            if (hasHeadings)
            {
                switch (section)
                {
                    case Section.Intro:
                        if (title is null)
                        {
                            title = TitleText(raw);
                        }
                        else
                        {
                            intro.Add(raw);
                        }

                        break;
                    case Section.Ingredients:
                        ingredients.Add(raw);
                        break;
                    default:
                        steps.Add(raw);
                        break;
                }

                continue;
            }

            // No headings: title first, then ingredient-looking lines, then steps from the first sentence on.
            if (title is null)
            {
                title = TitleText(raw);
            }
            else if (section == Section.Steps)
            {
                steps.Add(raw);
            }
            else if (LooksLikeIngredient(raw))
            {
                section = Section.Ingredients;
                seenIngredient = true;
                ingredients.Add(raw);
            }
            else if (raw.EndsWith(':') && !seenIngredient)
            {
                // A label such as "Du trenger:" before the ingredients.
            }
            else if (raw.EndsWith(':') && seenIngredient)
            {
                ingredients.Add(raw);
            }
            else
            {
                section = Section.Steps;
                steps.Add(raw);
            }
        }

        if (ingredients.Count == 0)
        {
            warnings.Add("No ingredients were found in the text.");
        }

        if (steps.Count == 0)
        {
            warnings.Add("No steps were found in the text.");
        }

        var description = intro.Count == 0 ? null : StepTextCleaner.Clean(string.Join(' ', intro), Recipe.MaxDescriptionLength);
        return new ImportedRecipe(
            title, description is { Length: 0 } ? null : description, servings, prep, cook, null, ingredients,
            [.. steps.Select(s => StepTextCleaner.Clean(s)).Where(s => s.Length > 0)], warnings);
    }

    private static string HeadingText(string line) => line.Trim().Trim('#', '*', '_', ':', '-', ' ').Trim();

    private static string TitleText(string line) => StepTextCleaner.Clean(line.TrimStart('#', ' ').Trim('*', '_', ' '), Recipe.MaxTitleLength);

    private static bool TryMeta(string line, ref int? servings, ref int? prep, ref int? cook)
    {
        // Meta lines are short ("Til 4 personer", "Koketid: 1 time 15 min"); a long line is a sentence that happens to start with such a word.
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words > 6)
        {
            return false;
        }

        if (words <= 4 && ServingsPrefixRegex().Match(line) is { Success: true } a && TryServings(a, out var s1))
        {
            servings ??= s1;
            return true;
        }

        if (words <= 4 && ServingsSuffixRegex().Match(line) is { Success: true } b && TryServings(b, out var s2))
        {
            servings ??= s2;
            return true;
        }

        if (PrepRegex().Match(line) is { Success: true } p && Durations.FromText(p.Groups["t"].Value) is { } prepMinutes)
        {
            prep ??= prepMinutes;
            return true;
        }

        if (CookRegex().Match(line) is { Success: true } c && Durations.FromText(c.Groups["t"].Value) is { } cookMinutes)
        {
            cook ??= cookMinutes;
            return true;
        }

        return false;
    }

    private static bool TryServings(Match match, out int servings)
    {
        servings = 0;
        var text = match.Groups["m"].Success ? match.Groups["m"].Value : match.Groups["n"].Value;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out servings) && servings > 0;
    }

    /// <summary>Short, not a sentence, and either starting with an amount or a few words (salt, "1 løk").</summary>
    private static bool LooksLikeIngredient(string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words > 8 || line.EndsWith('.') || line.EndsWith('!') || NumberedStepRegex().IsMatch(line))
        {
            return false;
        }

        var parsed = IngredientLineParser.Parse(line);
        return parsed.Kind == ParsedLineKind.Parsed && (parsed.Quantity is not null || words <= 3);
    }
}
