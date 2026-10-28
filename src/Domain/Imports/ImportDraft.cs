using System.Text.Json;
using System.Text.Json.Nodes;
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
/// <see cref="UnitId"/>, <see cref="Note"/> and the T-076 fields only. <see cref="AmountKind"/> says how to read the amount:
/// Measured needs quantity and unit; ToTaste and AsNeeded have neither. <see cref="AlternativeIds"/> are catalog or own
/// ingredients (ids are re-resolved through the filtered sets when the draft is saved); alternatives that did not match are
/// text in the note.
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
    IReadOnlyList<IngredientSuggestion> Suggestions,
    AmountKind AmountKind = AmountKind.Measured,
    IngredientPurpose Purpose = IngredientPurpose.Main,
    bool IsOptional = false,
    IReadOnlyList<Guid>? AlternativeIds = null)
{
    [JsonIgnore]
    public IReadOnlyList<Guid> Alternatives => AlternativeIds ?? [];

    [JsonIgnore]
    public ImportLineStatus Status =>
        IngredientId is null ? (Unparsed ? ImportLineStatus.Unparsed : ImportLineStatus.UnmatchedIngredient)
        : AmountRules.Check(AmountKind, Quantity, UnitId) is not null ? ImportLineStatus.UnmatchedUnit
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
        JsonSerializer.Deserialize<ImportDraft>(WithAmountKinds(json), Options) ?? throw new JsonException("The draft is empty.");

    /// <summary>
    /// A draft written before T-076 has no <c>amountKind</c> on its lines: a line without a quantity was "to taste", the
    /// others are measured. Added here so such a draft still reads correctly.
    /// </summary>
    private static string WithAmountKinds(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject draft || draft["lines"] is not JsonArray lines)
        {
            return json;
        }

        var changed = false;
        foreach (var line in lines.OfType<JsonObject>())
        {
            if (!line.ContainsKey("amountKind"))
            {
                line["amountKind"] = line["quantity"] is null ? nameof(AmountKind.ToTaste) : nameof(AmountKind.Measured);
                changed = true;
            }
        }

        return changed ? draft.ToJsonString() : json;
    }
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

        var text = line.IngredientText;
        var match = matcher.Match(text);
        var alternatives = line.Alternatives;
        if (match.IngredientId is null && line.UnsplitText is not null)
        {
            if (matcher.Match(line.UnsplitText) is { IngredientId: not null } whole)
            {
                // The split was wrong: the whole text is one ingredient.
                match = whole;
                text = line.UnsplitText;
                alternatives = [];
            }
            else if (!text!.Contains(' ') && alternatives.Count > 0 && alternatives[0].Text.Contains(' '))
            {
                // "rød eller gul paprika": the first part is only an adjective of the last word(s). Try "rød paprika".
                var completed = $"{text} {string.Join(' ', alternatives[0].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))}";
                if (matcher.Match(completed) is { IngredientId: not null } adjective)
                {
                    match = adjective;
                    text = completed;
                }
            }
        }

        // Alternatives that match the catalog become structured ids; the others stay in the note as text (never free-text entities).
        var alternativeIds = new List<Guid>();
        var notes = new List<string>();
        if (line.Note is not null)
        {
            notes.Add(line.Note);
        }

        foreach (var alternative in alternatives)
        {
            var id = MatchAlternative(matcher, alternative.Text);
            if (id is { } found && found != match.IngredientId && !alternativeIds.Contains(found)
                && alternativeIds.Count < RecipeIngredientAlternative.MaxPerLine)
            {
                alternativeIds.Add(found);
            }
            else
            {
                notes.Add(alternative.NoteText);
            }
        }

        var note = notes.Count == 0 ? null : string.Join("; ", notes);
        if (note is { Length: > RecipeIngredient.MaxNoteLength })
        {
            note = note[..RecipeIngredient.MaxNoteLength].TrimEnd();
        }

        var unitId = line.UnitCode is null ? null : unitIdByCode(line.UnitCode);
        return new ImportDraftLine(
            line.Original, text, line.Quantity, line.QuantityLow, line.UnitCode, line.UnitText, unitId,
            match.IngredientId, match.Confidence, note, false, match.Suggestions,
            line.AmountKind, line.Purpose, line.IsOptional, alternativeIds);
    }

    private static readonly System.Text.RegularExpressions.Regex GenericPrefix = new(
        @"^(?:annen|annet|andre|other|another|any\s+other)\s+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The catalog ingredient for an alternative's text. A generic alternative ("annen kokt hvit fisk") is tried as the text
    /// without "annen", then with the leading words dropped one at a time ("hvit fisk", "fisk").
    /// </summary>
    private static Guid? MatchAlternative(IngredientMatcher matcher, string text)
    {
        if (matcher.Match(text).IngredientId is { } direct)
        {
            return direct;
        }

        if (!GenericPrefix.IsMatch(text))
        {
            return null;
        }

        var words = GenericPrefix.Replace(text, string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var skip = 0; skip < words.Length; skip++)
        {
            if (matcher.Match(string.Join(' ', words.Skip(skip))).IngredientId is { } id)
            {
                return id;
            }
        }

        return null;
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
