using System.Text.Json;
using System.Text.Json.Serialization;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Imports;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImportLineStatus
{
    /// <summary>Ingredient and (quantity and unit, or neither: to taste) are known; the line can be saved.</summary>
    Matched,

    /// <summary>No catalog ingredient reached the match threshold.</summary>
    UnmatchedIngredient,

    /// <summary>There is a quantity but no catalog unit for it (an unknown unit word, or none chosen yet).</summary>
    UnmatchedUnit,

    /// <summary>The line could not be read at all; only <see cref="ImportDraftLine.Original"/> is known.</summary>
    Unparsed,
}

/// <summary>
/// One ingredient line of a draft. <see cref="Original"/> is the text as imported and exists only here, in the import
/// artifact (CLAUDE.md rule 5): saving the draft writes a recipe from <see cref="IngredientId"/>, <see cref="Quantity"/>,
/// <see cref="UnitId"/> and <see cref="Note"/> only. Quantity and unit both null is "to taste".
/// </summary>
public sealed record ImportDraftLine(
    string Original,
    string? IngredientText,
    decimal? Quantity,
    decimal? QuantityLow,
    string? UnitCode,
    string? UnitText,
    Guid? UnitId,
    Guid? IngredientId,
    double Confidence,
    string? Note,
    bool Unparsed,
    IReadOnlyList<IngredientSuggestion> Suggestions)
{
    [JsonIgnore]
    public ImportLineStatus Status =>
        IngredientId is null ? (Unparsed ? ImportLineStatus.Unparsed : ImportLineStatus.UnmatchedIngredient)
        : (Quantity is null) != (UnitId is null) ? ImportLineStatus.UnmatchedUnit
        : ImportLineStatus.Matched;
}

/// <summary>The parsed, matched and still editable recipe of an import. Stored as JSON on the import (no foreign keys).</summary>
public sealed record ImportDraft(
    string Title,
    string? Description,
    int? Servings,
    int? PrepMinutes,
    int? CookMinutes,
    string? ImageUrl,
    IReadOnlyList<string> Steps,
    IReadOnlyList<ImportDraftLine> Lines,
    IReadOnlyList<string> Warnings)
{
    [JsonIgnore]
    public bool AllLinesMatched => Lines.All(l => l.Status == ImportLineStatus.Matched);
}

public static class ImportDraftJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(ImportDraft draft) => JsonSerializer.Serialize(draft, Options);

    public static ImportDraft Deserialize(string json) =>
        JsonSerializer.Deserialize<ImportDraft>(json, Options) ?? throw new JsonException("The draft is empty.");
}

/// <summary>Turns an <see cref="ImportedRecipe"/> into a draft: parses each line, matches it, maps its unit code to a unit id.</summary>
public static class ImportDraftBuilder
{
    public const int DefaultServings = 4;

    /// <param name="unitIdByCode">The id of the catalog unit with this code, or null when the catalog has none (flagged "unmatched unit").</param>
    public static ImportDraft Build(ImportedRecipe imported, IngredientMatcher matcher, Func<string, Guid?> unitIdByCode)
    {
        var warnings = new List<string>(imported.Warnings);

        var title = StepTextCleaner.Clean(imported.Title, Recipe.MaxTitleLength);
        if (title.Length == 0)
        {
            warnings.Add("No title was found; give the recipe a title.");
        }

        var servings = imported.Servings;
        if (servings is null)
        {
            servings = DefaultServings;
            warnings.Add($"No servings were found; {DefaultServings} assumed.");
        }

        var prep = Minutes(imported.PrepMinutes, "prep time", warnings);
        var cook = Minutes(imported.CookMinutes, "cooking time", warnings);

        var parsed = IngredientLineParser.ParseAll(imported.IngredientLines);
        if (parsed.Count > Recipe.MaxIngredients)
        {
            warnings.Add($"Only the first {Recipe.MaxIngredients} ingredient lines were kept.");
            parsed = [.. parsed.Take(Recipe.MaxIngredients)];
        }

        var lines = parsed.Select(line => ToDraftLine(line, matcher, unitIdByCode)).ToList();

        var steps = imported.Steps.Select(s => StepTextCleaner.Clean(s)).Where(s => s.Length > 0).ToList();
        if (steps.Count > Recipe.MaxSteps)
        {
            warnings.Add($"Only the first {Recipe.MaxSteps} steps were kept.");
            steps = [.. steps.Take(Recipe.MaxSteps)];
        }

        var description = StepTextCleaner.Clean(imported.Description, Recipe.MaxDescriptionLength);
        return new ImportDraft(
            title, description.Length == 0 ? null : description, servings, prep, cook, imported.ImageUrl, steps, lines, warnings);
    }

    private static ImportDraftLine ToDraftLine(ParsedIngredientLine line, IngredientMatcher matcher, Func<string, Guid?> unitIdByCode)
    {
        if (line.Kind != ParsedLineKind.Parsed || line.IngredientText is null)
        {
            return new ImportDraftLine(line.Original, null, null, null, null, null, null, null, 0, null, true, []);
        }

        var match = matcher.Match(line.IngredientText);
        var unitId = line.UnitCode is null ? null : unitIdByCode(line.UnitCode);
        return new ImportDraftLine(
            line.Original, line.IngredientText, line.Quantity, line.QuantityLow, line.UnitCode, line.UnitText, unitId,
            match.IngredientId, match.Confidence, line.Note, false, match.Suggestions);
    }

    private static int? Minutes(int? minutes, string what, List<string> warnings)
    {
        if (minutes is > Recipe.MaxMinutes)
        {
            warnings.Add($"The {what} was longer than {Recipe.MaxMinutes} minutes and was left out.");
            return null;
        }

        return minutes;
    }
}
