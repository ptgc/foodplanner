using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Imports;

/// <summary>The dish picture Claude found, as it said it: still untrusted. The box is normalized 0..1 on the photo as sent.</summary>
public sealed record PhotoPicture(int PhotoIndex, double X, double Y, double W, double H, int RotationDegrees);

/// <summary>One ingredient line as Claude read it from the page. Untrusted text; <see cref="Original"/> is the page text of the line.</summary>
public sealed record PhotoIngredient(
    string? Original,
    string? Ingredient,
    string? AmountKind,
    decimal? Quantity,
    decimal? QuantityLow,
    string? Unit,
    string? Purpose,
    bool IsOptional,
    IReadOnlyList<string>? Alternatives,
    string? Note);

/// <summary>The validated shape of Claude's answer for photos of a recipe (T-118). Every value is still untrusted content.</summary>
public sealed record PhotoRecipe(
    bool IsRecipe,
    bool Readable,
    string? Title,
    int? Servings,
    int? PrepMinutes,
    int? CookMinutes,
    string? Description,
    string? SourceBook,
    string? RawText,
    IReadOnlyList<string> Steps,
    IReadOnlyList<PhotoIngredient> Ingredients,
    PhotoPicture? Picture,
    IReadOnlyList<string> Warnings);

/// <summary>
/// A dish picture box that passed the checks (T-118): finite values in 0..1, at least <see cref="MinSide"/> wide and high, inside
/// the photo (a hair over is clamped), one of the sent photos, a right-angle rotation. Otherwise there is simply no suggested picture.
/// </summary>
public sealed record PictureCrop(int PhotoIndex, double X, double Y, double W, double H, int RotationDegrees)
{
    public const double MinSide = 0.10;
    public const double Tolerance = 1.001;

    /// <summary>A crop whose longer side is under this many pixels is too small to be a picture.</summary>
    public const int MinLongSidePixels = 320;

    public static PictureCrop? From(PhotoPicture? picture, int photoCount)
    {
        if (picture is null || picture.PhotoIndex < 0 || picture.PhotoIndex >= photoCount
            || picture.RotationDegrees is not (0 or 90 or 180 or 270))
        {
            return null;
        }

        double[] values = [picture.X, picture.Y, picture.W, picture.H];
        if (values.Any(v => !double.IsFinite(v) || v < 0 || v > 1) || picture.W < MinSide || picture.H < MinSide
            || picture.X + picture.W > Tolerance || picture.Y + picture.H > Tolerance)
        {
            return null;
        }

        var w = Math.Min(picture.W, 1 - picture.X);
        var h = Math.Min(picture.H, 1 - picture.Y);
        return w < MinSide || h < MinSide ? null : new PictureCrop(picture.PhotoIndex, picture.X, picture.Y, w, h, picture.RotationDegrees);
    }
}

/// <summary>The draft, the page text Claude read (the import artifact) and the picture box that may be cropped.</summary>
public sealed record PhotoMapResult(ImportDraft Draft, string RawText, PictureCrop? Crop);

/// <summary>
/// Turns Claude's reading of recipe photos into an import draft (T-118). Pure. Unlike the T-077 fallback no line is dropped
/// silently: a line that passes the shared rules of <see cref="AiSuggestionMerger"/> is Matched; any other stays in the draft
/// as an unmatched ingredient or unit (with the page text in <c>Original</c> and the matcher's suggestions) so the person can fix
/// it in the review. Every line is marked AI-suggested.
/// </summary>
public static class PhotoRecipeMapper
{
    public const int MaxWarnings = 10;
    public const int MaxWarningLength = 200;

    public static PhotoMapResult Map(
        PhotoRecipe recipe, string? typedSourceName, int photoCount, IngredientMatcher matcher, Func<string, Guid?> unitIdByCode)
    {
        var warnings = new List<string>();
        foreach (var warning in recipe.Warnings.Take(MaxWarnings))
        {
            var cleaned = StepTextCleaner.Clean(warning, MaxWarningLength);
            if (cleaned.Length > 0)
            {
                warnings.Add(cleaned);
            }
        }

        var title = StepTextCleaner.Clean(recipe.Title, Recipe.MaxTitleLength);
        if (title.Length == 0)
        {
            warnings.Add("No title was found; give the recipe a title.");
        }

        var servings = recipe.Servings;
        if (servings is null or < Recipe.MinServings or > Recipe.MaxServings)
        {
            servings = ImportDraftBuilder.DefaultServings;
            warnings.Add($"No servings were found; {ImportDraftBuilder.DefaultServings} assumed.");
        }

        var prep = Minutes(recipe.PrepMinutes, "prep time", warnings);
        var cook = Minutes(recipe.CookMinutes, "cooking time", warnings);

        var ingredients = recipe.Ingredients;
        if (ingredients.Count > Recipe.MaxIngredients)
        {
            warnings.Add($"Only the first {Recipe.MaxIngredients} ingredient lines were kept.");
            ingredients = [.. ingredients.Take(Recipe.MaxIngredients)];
        }

        var lines = ingredients.Select(i => ToLine(i, matcher, unitIdByCode)).ToList();

        var steps = recipe.Steps.Select(s => StepTextCleaner.Clean(s)).Where(s => s.Length > 0).ToList();
        if (steps.Count > Recipe.MaxSteps)
        {
            warnings.Add($"Only the first {Recipe.MaxSteps} steps were kept.");
            steps = [.. steps.Take(Recipe.MaxSteps)];
        }

        var description = StepTextCleaner.Clean(recipe.Description, Recipe.MaxDescriptionLength);
        var sourceName = SourceName(typedSourceName, recipe.SourceBook, warnings);
        var raw = (recipe.RawText ?? string.Empty).Trim();
        if (raw.Length > RecipeImport.MaxRawLength)
        {
            raw = raw[..RecipeImport.MaxRawLength];
        }

        var crop = PictureCrop.From(recipe.Picture, photoCount);
        var draft = new ImportDraft(
            title, description.Length == 0 ? null : description, servings, prep, cook, null, steps, lines, warnings,
            AiAssistStatus.Applied, sourceName);
        return new PhotoMapResult(draft, raw, crop);
    }

    /// <summary>What the person typed wins over what Claude read on the page; at most <see cref="Recipe.MaxSourceNameLength"/> characters.</summary>
    private static string? SourceName(string? typed, string? read, List<string> warnings)
    {
        var name = StepTextCleaner.Clean(string.IsNullOrWhiteSpace(typed) ? read : typed, int.MaxValue);
        if (name.Length == 0)
        {
            return null;
        }

        if (name.Length > Recipe.MaxSourceNameLength)
        {
            warnings.Add($"The book name was cut to {Recipe.MaxSourceNameLength} characters.");
            name = name[..Recipe.MaxSourceNameLength].TrimEnd();
        }

        return name;
    }

    private static int? Minutes(int? minutes, string what, List<string> warnings)
    {
        if (minutes is > Recipe.MaxMinutes)
        {
            warnings.Add($"The {what} was longer than {Recipe.MaxMinutes} minutes and was left out.");
            return null;
        }

        return minutes is < 0 ? null : minutes;
    }

    private static ImportDraftLine ToLine(PhotoIngredient i, IngredientMatcher matcher, Func<string, Guid?> unitIdByCode)
    {
        var name = Plain(i.Ingredient, AiSuggestionMerger.MaxNameLength);
        var original = Plain(i.Original, AiSuggestionMerger.MaxLineTextLength);
        if (original.Length == 0)
        {
            original = name;
        }

        var suggestion = new AiLineSuggestion(0, i.Ingredient, i.Quantity, i.QuantityLow, i.Unit, i.AmountKind, i.Purpose, i.IsOptional, i.Alternatives, i.Note);
        var empty = new ImportDraftLine(original, name, null, null, null, null, null, null, 0, null, false, []);
        return AiSuggestionMerger.Build(empty, suggestion, matcher, unitIdByCode) ?? Fallback(empty, i, matcher, unitIdByCode);
    }

    /// <summary>
    /// A line that failed a rule stays, never matched: the person must look at it. The parts that are valid are kept (a good
    /// quantity, a known unit, a matched ingredient); the rest is left for the review controls.
    /// </summary>
    private static ImportDraftLine Fallback(ImportDraftLine empty, PhotoIngredient i, IngredientMatcher matcher, Func<string, Guid?> unitIdByCode)
    {
        var text = empty.IngredientText is { Length: > 0 } ? empty.IngredientText : Plain(empty.Original, AiSuggestionMerger.MaxNameLength);
        var match = text.Length == 0 ? new IngredientMatchResult(null, null, 0, []) : matcher.Match(text);

        var kind = AmountKind.Measured;
        if (!string.IsNullOrWhiteSpace(i.AmountKind) && AmountKinds.TryParse(i.AmountKind.Trim(), out var parsedKind))
        {
            kind = parsedKind;
        }
        else if (string.IsNullOrWhiteSpace(i.AmountKind))
        {
            kind = AmountRules.Infer(i.Quantity);
        }

        decimal? quantity = null;
        Guid? unitId = null;
        string? unitCode = null;
        string? unitText = null;
        if (kind == AmountKind.Measured)
        {
            if (AiSuggestionMerger.ValidQuantity(i.Quantity, out var rounded))
            {
                quantity = rounded;
            }

            if (!string.IsNullOrWhiteSpace(i.Unit))
            {
                var code = i.Unit.Trim();
                unitId = unitIdByCode(code) ?? unitIdByCode(code.ToLowerInvariant());
                if (unitId is null)
                {
                    unitText = Plain(code, 30);
                }
                else
                {
                    unitCode = code.ToLowerInvariant();
                }
            }
        }

        var purpose = IngredientPurpose.Main;
        if (!string.IsNullOrWhiteSpace(i.Purpose) && IngredientPurposes.TryParse(i.Purpose.Trim(), out var parsedPurpose))
        {
            purpose = parsedPurpose;
        }

        var note = AiSuggestionMerger.CleanNote(i.Note);
        if (note is { Length: > RecipeIngredient.MaxNoteLength })
        {
            note = note[..RecipeIngredient.MaxNoteLength].TrimEnd();
        }

        var line = empty with
        {
            IngredientText = text.Length == 0 ? null : text,
            Quantity = quantity,
            UnitCode = unitCode,
            UnitText = unitText,
            UnitId = unitId,
            IngredientId = match.IngredientId,
            Confidence = match.Confidence,
            Note = note,
            Suggestions = match.Suggestions,
            AmountKind = kind,
            Purpose = purpose,
            IsOptional = i.IsOptional,
            AiSuggested = true,
        };

        if (line.Status != ImportLineStatus.Matched)
        {
            return line;
        }

        // Valid in itself but the strict rules refused it (for example a purpose that is not one of the five): the person confirms the ingredient.
        IngredientSuggestion[] first = match.IngredientId is { } id && match.Name is { } matchedName
            ? [new IngredientSuggestion(id, matchedName, match.Confidence)]
            : [];
        return line with { IngredientId = null, Suggestions = [.. first, .. match.Suggestions.Where(s => s.IngredientId != match.IngredientId)] };
    }

    private static string Plain(string? text, int max)
    {
        var cleaned = StepTextCleaner.Clean(text, max);
        return cleaned;
    }
}
