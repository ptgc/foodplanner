using System.Globalization;
using System.Text.RegularExpressions;

namespace Foodplanner.Domain.Imports;

/// <summary>
/// What a source (a page's JSON-LD or pasted text) says about a recipe, before any matching: the ingredient lines are
/// still text. <see cref="ImageUrl"/> is only a candidate; nothing downloads it.
/// </summary>
public sealed record ImportedRecipe(
    string? Title,
    string? Description,
    int? Servings,
    int? PrepMinutes,
    int? CookMinutes,
    string? ImageUrl,
    IReadOnlyList<string> IngredientLines,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings);

/// <summary>Durations to whole minutes: ISO 8601 (schema.org: PT1H30M, P0DT45M) and plain text (1 time 15 min, 1h 30m, 45 minutter).</summary>
public static partial class Durations
{
    [GeneratedRegex(@"^P(?:(?<w>\d+)W)?(?:(?<d>\d+)D)?(?:T(?:(?<h>\d+(?:[.,]\d+)?)H)?(?:(?<m>\d+(?:[.,]\d+)?)M)?(?:(?<s>\d+(?:[.,]\d+)?)S)?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsoRegex();

    [GeneratedRegex(@"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>timer|time|hours?|hrs?|h|t)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HoursRegex();

    [GeneratedRegex(@"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>minutter|minutt|minutes?|mins?|min|m)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MinutesRegex();

    /// <summary>Minutes of an ISO 8601 duration, rounded; null when it is not one or is zero.</summary>
    public static int? FromIso(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var m = IsoRegex().Match(value.Trim());
        if (!m.Success || m.Value.Length <= 1 || m.Value.EndsWith('T'))
        {
            return null;
        }

        var minutes = Number(m.Groups["w"]) * 10080 + Number(m.Groups["d"]) * 1440 + Number(m.Groups["h"]) * 60
            + Number(m.Groups["m"]) + Number(m.Groups["s"]) / 60;
        return ToMinutes(minutes);
    }

    /// <summary>Minutes in free text such as "1 time 15 min" or "45 minutter"; null when none is found.</summary>
    public static int? FromText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var hours = HoursRegex().Matches(value).Sum(match => Number(match.Groups["n"]));
        var minutes = MinutesRegex().Matches(value).Sum(match => Number(match.Groups["n"]));
        return ToMinutes(hours * 60 + minutes);
    }

    private static decimal Number(Group group) =>
        group.Success && decimal.TryParse(group.Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static int? ToMinutes(decimal minutes)
    {
        if (minutes <= 0 || minutes > 100_000)
        {
            return null;
        }

        var rounded = (int)decimal.Round(minutes, MidpointRounding.AwayFromZero);
        return rounded > 0 ? rounded : null;
    }
}
