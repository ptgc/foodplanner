using System.Text.Json.Serialization;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Imports;

/// <summary>What the AI fallback (T-077) did for an import. Stored on the draft so the review screen can say why a line was not filled in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiAssistStatus
{
    /// <summary>The feature is off, or there was nothing unresolved to send.</summary>
    NotUsed,

    /// <summary>The AI was asked and answered; lines it filled in are marked <see cref="ImportDraftLine.AiSuggested"/>.</summary>
    Applied,

    /// <summary>A per-household or global usage limit was reached; the rule-based result stands.</summary>
    LimitReached,

    /// <summary>The call failed (timeout, HTTP error, unreadable answer); the rule-based result stands.</summary>
    Unavailable,
}

/// <summary>
/// One line as the AI returned it, still untrusted text. Ingredient and alternatives are names (the AI never sees ids) that are
/// matched against the catalog the household can see; <see cref="Unit"/> is a catalog unit code.
/// <see cref="Index"/> is the position of the line in the request (0-based), not in the draft.
/// </summary>
public sealed record AiLineSuggestion(
    int Index,
    string? Ingredient,
    decimal? Quantity,
    decimal? QuantityLow,
    string? Unit,
    string? AmountKind,
    string? Purpose,
    bool IsOptional,
    IReadOnlyList<string>? Alternatives,
    string? Note);

/// <summary>The merged draft, how many suggested lines were taken, and how many were dropped as invalid.</summary>
public sealed record AiMergeResult(ImportDraft Draft, int Applied, int Dropped);

/// <summary>
/// Picks the lines to send to the AI fallback and merges its answer into the draft (T-077). The answer is never trusted: a line
/// is taken only if every part of it passes the domain rules (ingredient found in the catalog, unit known, amount consistent with
/// its kind, quantity in range); a line that fails any check is dropped whole and the rule-based line stays. Pure: the caller does
/// the HTTP call, the limits and the catalog loading.
/// </summary>
public static class AiSuggestionMerger
{
    public const int MaxLinesPerCall = 30;
    public const int MaxLineTextLength = 300;
    public const int MaxNameLength = 100;

    /// <summary>
    /// The draft indexes of the lines the rules could not resolve (not <see cref="ImportLineStatus.Matched"/>: unparsed, no
    /// ingredient match, or no unit), at most <see cref="MaxLinesPerCall"/>, in order. A line without any original text is skipped.
    /// </summary>
    public static IReadOnlyList<int> Unresolved(ImportDraft draft) =>
        [.. draft.Lines.Select((line, index) => (line, index))
            .Where(x => x.line.Status != ImportLineStatus.Matched && !string.IsNullOrWhiteSpace(x.line.Original))
            .Select(x => x.index).Take(MaxLinesPerCall)];

    /// <summary>The only text that leaves the server for a line: its original ingredient text, trimmed and cut.</summary>
    public static string RequestText(ImportDraftLine line)
    {
        var text = new string([.. line.Original.Where(c => !char.IsControl(c) || c == ' ')]).Trim();
        return text.Length <= MaxLineTextLength ? text : text[..MaxLineTextLength];
    }

    /// <param name="sentIndexes">Draft indexes of the lines that were sent, in request order; <see cref="AiLineSuggestion.Index"/> points into it.</param>
    /// <param name="matcher">The household's visible catalog.</param>
    /// <param name="unitIdByCode">The catalog unit with this code, or null.</param>
    public static AiMergeResult Merge(
        ImportDraft draft,
        IReadOnlyList<int> sentIndexes,
        IEnumerable<AiLineSuggestion?> suggestions,
        IngredientMatcher matcher,
        Func<string, Guid?> unitIdByCode)
    {
        var lines = draft.Lines.ToList();
        var seen = new HashSet<int>();
        int applied = 0, dropped = 0;
        foreach (var suggestion in suggestions)
        {
            if (suggestion is null || suggestion.Index < 0 || suggestion.Index >= sentIndexes.Count || !seen.Add(suggestion.Index))
            {
                dropped++;
                continue;
            }

            var at = sentIndexes[suggestion.Index];
            if (at < 0 || at >= lines.Count || Build(lines[at], suggestion, matcher, unitIdByCode) is not { } line)
            {
                dropped++;
                continue;
            }

            lines[at] = line;
            applied++;
        }

        return new AiMergeResult(draft with { Lines = lines }, applied, dropped);
    }

    private static ImportDraftLine? Build(ImportDraftLine current, AiLineSuggestion s, IngredientMatcher matcher, Func<string, Guid?> unitIdByCode)
    {
        if (!IsPlainName(s.Ingredient))
        {
            return null;
        }

        var match = matcher.Match(s.Ingredient);
        if (match.IngredientId is not { } ingredientId)
        {
            return null;
        }

        var kind = AmountRules.Infer(s.Quantity);
        if (!string.IsNullOrWhiteSpace(s.AmountKind) && !AmountKinds.TryParse(s.AmountKind.Trim(), out kind))
        {
            return null;
        }

        var purpose = IngredientPurpose.Main;
        if (!string.IsNullOrWhiteSpace(s.Purpose) && !IngredientPurposes.TryParse(s.Purpose.Trim(), out purpose))
        {
            return null;
        }

        decimal? quantity = null;
        decimal? quantityLow = null;
        Guid? unitId = null;
        string? unitCode = null;
        if (kind == AmountKind.Measured)
        {
            if (!ValidQuantity(s.Quantity, out var rounded) || string.IsNullOrWhiteSpace(s.Unit))
            {
                return null;
            }

            unitCode = s.Unit.Trim();
            unitId = unitIdByCode(unitCode) ?? unitIdByCode(unitCode.ToLowerInvariant());
            if (unitId is null)
            {
                return null;
            }

            unitCode = unitCode.ToLowerInvariant();
            quantity = rounded;
            if (s.QuantityLow is not null)
            {
                if (!ValidQuantity(s.QuantityLow, out var low) || low >= rounded)
                {
                    return null;
                }

                quantityLow = low;
            }
        }
        else if (s.Quantity is not null || s.QuantityLow is not null || !string.IsNullOrWhiteSpace(s.Unit))
        {
            return null;
        }

        // Alternatives that match the catalog become ids; the others stay as text in the note (never free-text entities).
        var alternativeIds = new List<Guid>();
        var notes = new List<string>();
        if (CleanNote(s.Note) is { } own)
        {
            notes.Add(own);
        }

        foreach (var alternative in s.Alternatives ?? [])
        {
            if (!IsPlainName(alternative))
            {
                continue;
            }

            if (matcher.Match(alternative).IngredientId is { } id && id != ingredientId && !alternativeIds.Contains(id)
                && alternativeIds.Count < RecipeIngredientAlternative.MaxPerLine)
            {
                alternativeIds.Add(id);
            }
            else if (matcher.Match(alternative).IngredientId is null)
            {
                notes.Add(alternative.Trim());
            }
        }

        var note = notes.Count == 0 ? null : string.Join("; ", notes);
        if (note is { Length: > RecipeIngredient.MaxNoteLength })
        {
            note = note[..RecipeIngredient.MaxNoteLength].TrimEnd();
        }

        var result = current with
        {
            IngredientText = s.Ingredient!.Trim(),
            Quantity = quantity,
            QuantityLow = quantityLow,
            UnitCode = unitCode,
            UnitText = null,
            UnitId = unitId,
            IngredientId = ingredientId,
            Confidence = match.Confidence,
            Note = note,
            Unparsed = false,
            Suggestions = [],
            AmountKind = kind,
            Purpose = purpose,
            IsOptional = s.IsOptional,
            AlternativeIds = alternativeIds,
            AiSuggested = true,
        };

        // The same rule the save applies: a line the AI fills in must be fully usable, or it is not taken.
        return result.Status == ImportLineStatus.Matched ? result : null;
    }

    private static bool ValidQuantity(decimal? value, out decimal rounded)
    {
        rounded = 0;
        if (value is not { } v || v <= 0 || v > RecipeIngredient.MaxQuantity)
        {
            return false;
        }

        rounded = decimal.Round(v, RecipeIngredient.MaxQuantityDecimals, MidpointRounding.AwayFromZero);
        return rounded > 0;
    }

    private static bool IsPlainName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength && !name.Any(char.IsControl);

    private static string? CleanNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var cleaned = new string([.. note.Where(c => !char.IsControl(c))]).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
