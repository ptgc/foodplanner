namespace Foodplanner.Domain.Imports;

/// <summary>
/// Unit words found in Norwegian and English recipe lines, mapped to the codes of the seeded units
/// (<see cref="Catalog.UnitSeed"/>). A word in the unmapped set ("boks", "oz") is clearly a unit that the catalog does
/// not have: the line is kept with that text and flagged "unmatched unit" for manual mapping.
/// </summary>
public static class UnitWords
{
    private static readonly Dictionary<string, string> Aliases = Build(
        ("g", ["g", "gr", "gram", "grams"]),
        ("kg", ["kg", "kilo", "kilos", "kilogram", "kilograms"]),
        ("ml", ["ml", "milliliter", "milliliters", "millilitre", "millilitres", "millilitrer"]),
        ("dl", ["dl", "desiliter", "desiliters", "desilitre", "deciliter", "deciliters", "decilitre", "decilitres"]),
        ("l", ["l", "liter", "liters", "litre", "litres", "ltr"]),
        ("ts", ["ts", "tsk", "teskje", "teskjeer", "teskjeen", "tsp", "tspn", "teaspoon", "teaspoons"]),
        ("ss", ["ss", "spiseskje", "spiseskjeer", "spiseskjeen", "tbsp", "tbs", "tbspn", "tablespoon", "tablespoons"]),
        ("kopp", ["kopp", "kopper", "cup", "cups"]),
        ("stk", ["stk", "st", "stykk", "stykke", "stykker", "pcs", "pc", "piece", "pieces"]),
        ("fedd", ["fedd", "clove", "cloves"]),
        ("klype", ["klype", "klyper", "pinch", "pinches", "knivsodd"]));

    /// <summary>Recognised as units, but not in the catalog: flagged for manual mapping.</summary>
    private static readonly HashSet<string> UnmappedWords = new(StringComparer.Ordinal)
    {
        "boks", "bokser", "pk", "pakke", "pakker", "pose", "poser", "neve", "never", "dæsj", "dæsjer", "skive", "skiver",
        "bunt", "bunter", "kvist", "kvister", "stang", "stenger", "blad", "glass", "flaske", "flasker", "knippe", "hode", "hoder",
        "cl", "oz", "ounce", "ounces", "lb", "lbs", "pound", "pounds", "pint", "pints", "quart", "quarts", "gallon", "gallons",
        "can", "cans", "tin", "tins", "pkg", "package", "packages", "packet", "packets", "bag", "bags", "handful", "handfuls",
        "dash", "dashes", "slice", "slices", "bunch", "bunches", "sprig", "sprigs", "stalk", "stalks", "head", "heads",
        "stick", "sticks", "jar", "jars", "bottle", "bottles", "drop", "drops", "knob", "potte", "potter", "pot", "pots",
    };

    /// <summary>The seeded unit code for a unit word (case-insensitive, a trailing dot ignored), or null.</summary>
    public static string? CodeFor(string word) =>
        Aliases.TryGetValue(word.Trim().TrimEnd('.').ToLowerInvariant(), out var code) ? code : null;

    /// <summary>True for a word that is a unit but not in the catalog.</summary>
    public static bool IsUnmapped(string word) => UnmappedWords.Contains(word.Trim().TrimEnd('.').ToLowerInvariant());

    public static bool IsUnitWord(string word) => CodeFor(word) is not null || IsUnmapped(word);

    private static Dictionary<string, string> Build(params (string Code, string[] Words)[] groups)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, words) in groups)
        {
            foreach (var word in words)
            {
                map.Add(word, code);
            }
        }

        return map;
    }
}
