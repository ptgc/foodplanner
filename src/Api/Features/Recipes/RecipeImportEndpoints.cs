using Foodplanner.Api.Authorization;
using Foodplanner.Api.Localization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Security;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Imports;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Import;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Localization;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Recipes;

/// <summary>Exactly one of <c>Url</c> (a page with schema.org Recipe JSON-LD) or <c>Text</c> (pasted recipe text).</summary>
public sealed record CreateRecipeImportRequest(string? Url, string? Text);

public sealed record ImportSuggestionResponse(Guid IngredientId, string Name, double Confidence);

/// <summary>
/// One ingredient line of a draft. <c>Original</c> is the imported text (the import artifact; recipes never store it).
/// <c>Status</c> says what is missing. A line with <c>Quantity</c> and <c>UnitId</c> both null is "to taste".
/// <c>UnitText</c> is a unit word the catalog lacks ("boks"); <c>UnitCode</c>/<c>UnitName</c> describe the chosen or parsed unit.
/// </summary>
public sealed record ImportLineResponse(
    int Position,
    string Original,
    string? IngredientText,
    decimal? Quantity,
    decimal? QuantityLow,
    string? UnitText,
    Guid? UnitId,
    string? UnitCode,
    string? UnitName,
    Guid? IngredientId,
    string? IngredientName,
    double Confidence,
    string? Note,
    ImportLineStatus Status,
    ImportSuggestionResponse[] Suggestions);

/// <summary><c>CanSave</c> is true when every line is <c>Matched</c>. <c>ImageUrl</c> is only a candidate; nothing is downloaded.</summary>
public sealed record RecipeImportResponse(
    Guid Id,
    ImportSource Source,
    string? SourceUrl,
    ImportStatus Status,
    string Title,
    string? Description,
    int? Servings,
    int? PrepMinutes,
    int? CookMinutes,
    string? ImageUrl,
    string[] Steps,
    ImportLineResponse[] Lines,
    string[] Warnings,
    bool CanSave,
    Guid? SavedRecipeId,
    DateTimeOffset CreatedAt);

public sealed record RecipeImportSummaryResponse(Guid Id, ImportSource Source, string? SourceUrl, string Title, DateTimeOffset CreatedAt);

/// <summary>
/// Edits a draft. A field that is absent (null) stays as it is: title, description, servings, times and steps change only when
/// sent (an empty string or list clears text and steps). <c>Lines</c> are edits of single lines by <c>Position</c>: lines not
/// listed stay as they are.
/// </summary>
public sealed record UpdateRecipeImportRequest(
    string? Title,
    string? Description,
    int? Servings,
    int? PrepMinutes,
    int? CookMinutes,
    string[]? Steps,
    UpdateImportLineRequest[]? Lines);

/// <summary>
/// Replaces the structured part of the line at <c>Position</c>: ingredient, quantity, unit and note (quantity and unit both
/// null is "to taste"), or removes it with <c>Remove</c>. To use an ingredient the household does not have yet, create it with
/// <c>POST /ingredients</c> first and pass its id.
/// </summary>
public sealed record UpdateImportLineRequest(int? Position, Guid? IngredientId, decimal? Quantity, Guid? UnitId, string? Note, bool? Remove);

public sealed record SaveRecipeImportResponse(Guid RecipeId);

/// <summary>
/// Recipe import (T-042): paste text or give a URL, review a draft with matched ingredients, fix what did not match, save as a
/// recipe through the normal recipe path. Adult or Owner only. The original text lives in the import artifact only
/// (CLAUDE.md rule 5). See docs/domain.md.
/// </summary>
public static class RecipeImportEndpoints
{
    public const int MaxTextLength = 100_000;
    public const int MaxDrafts = 50;

    public static IEndpointRouteBuilder MapRecipeImportEndpoints(this IEndpointRouteBuilder api)
    {
        var imports = api.MapHouseholdScopedGroup().MapGroup("/imports").RequireAuthorization(Policies.HouseholdAdult);

        imports.MapGet("", ListImports)
            .WithName("ListRecipeImports")
            .Produces<RecipeImportSummaryResponse[]>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        imports.MapPost("", CreateImport)
            .WithName("CreateRecipeImport")
            .Produces<RecipeImportResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        imports.MapGet("/{importId:guid}", GetImport)
            .WithName("GetRecipeImport")
            .Produces<RecipeImportResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        imports.MapPut("/{importId:guid}", UpdateImport)
            .WithName("UpdateRecipeImport")
            .Produces<RecipeImportResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        imports.MapPost("/{importId:guid}/save", SaveImport)
            .WithName("SaveRecipeImport")
            .Produces<SaveRecipeImportResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        imports.MapDelete("/{importId:guid}", DiscardImport)
            .WithName("DiscardRecipeImport")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    /// <summary>The drafts waiting for review (newest first), so an abandoned draft can be found and discarded.</summary>
    private static async Task<IResult> ListImports(AppDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.RecipeImports.AsNoTracking().Where(i => i.Status == ImportStatus.Draft)
            .OrderByDescending(i => EF.Property<DateTimeOffset>(i, "CreatedAt")).ThenBy(i => i.Id)
            .Select(i => new { i.Id, i.Source, i.SourceUrl, i.DraftJson, CreatedAt = EF.Property<DateTimeOffset>(i, "CreatedAt") })
            .Take(MaxDrafts).ToListAsync(cancellationToken);
        return Results.Ok(rows.Select(r => new RecipeImportSummaryResponse(
            r.Id, r.Source, r.SourceUrl, ImportDraftJson.Deserialize(r.DraftJson!).Title, r.CreatedAt)).ToArray());
    }

    private static async Task<IResult> CreateImport(
        Guid householdId, CreateRecipeImportRequest? request, RequestLocale locale, ICurrentUser user, IWebPageFetcher fetcher,
        UrlImportRateLimiter limiter, ILogger<RecipeImport> logger, AppDbContext db, CancellationToken cancellationToken)
    {
        var url = request?.Url?.Trim();
        var text = request?.Text;
        var errors = new Dictionary<string, string[]>();
        if (request is null || (string.IsNullOrEmpty(url)) == string.IsNullOrWhiteSpace(text))
        {
            errors["url"] = ["Give either a url or a text, not both and not neither."];
        }
        else if (url is { Length: > SafeWebPageFetcher.MaxUrlLength })
        {
            errors["url"] = [$"The URL can be at most {SafeWebPageFetcher.MaxUrlLength} characters."];
        }
        else if (text is { Length: > MaxTextLength })
        {
            errors["text"] = [$"The text can be at most {MaxTextLength} characters."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (user.UserId is not { } userId)
        {
            return AppProblems.Problem(StatusCodes.Status403Forbidden, AppProblems.Forbidden, "Forbidden");
        }

        if (await db.RecipeImports.CountAsync(i => i.Status == ImportStatus.Draft, cancellationToken) >= MaxDrafts)
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.ImportLimit,
                "Too many imports waiting",
                $"There are already {MaxDrafts} imports waiting for review. Save or discard some first.");
        }

        ImportedRecipe imported;
        string raw;
        if (!string.IsNullOrEmpty(url))
        {
            if (!limiter.TryAcquire(userId, out var retryAfter))
            {
                return new RetryAfterResult(retryAfter);
            }

            var fetched = await fetcher.FetchAsync(url, cancellationToken);
            if (!fetched.Succeeded)
            {
                logger.LogInformation("Recipe import fetch failed: {Failure}.", fetched.Failure);
                return FetchProblem(fetched);
            }

            var found = RecipeJsonLdExtractor.Extract(fetched.Content);
            if (found is null)
            {
                return AppProblems.Problem(
                    StatusCodes.Status422UnprocessableEntity,
                    AppProblems.ImportNoRecipeFound,
                    "No recipe found on the page",
                    "The page has no recipe data we can read (schema.org Recipe). Copy the recipe text from the page and paste it instead.");
            }

            imported = found.Recipe;
            raw = found.RawJson;
        }
        else
        {
            imported = PastedRecipeParser.Parse(text);
            raw = text!;
            if (imported.Title is null && imported.IngredientLines.Count == 0 && imported.Steps.Count == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["No recipe could be found in the text."] });
            }
        }

        var draft = ImportDraftBuilder.Build(imported, await LoadMatcherAsync(db, locale.Code, cancellationToken), await LoadUnitLookupAsync(db, cancellationToken));
        var import = new RecipeImport(
            string.IsNullOrEmpty(url) ? ImportSource.Text : ImportSource.Url, string.IsNullOrEmpty(url) ? null : url, raw,
            ImportDraftJson.Serialize(draft), userId);
        db.RecipeImports.Add(import);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/households/{householdId}/imports/{import.Id}", await ToResponseAsync(db, import, draft, locale.Code, cancellationToken));
    }

    private static async Task<IResult> GetImport(Guid importId, RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        var import = await FindAsync(db, importId, cancellationToken);
        return import is null
            ? NotFound()
            : Results.Ok(await ToResponseAsync(db, import, ImportDraftJson.Deserialize(import.DraftJson!), locale.Code, cancellationToken));
    }

    private static async Task<IResult> UpdateImport(
        Guid importId, UpdateRecipeImportRequest? request, RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        var import = await FindAsync(db, importId, cancellationToken);
        if (import is null)
        {
            return NotFound();
        }

        if (import.Status != ImportStatus.Draft)
        {
            return NotDraft(import);
        }

        var errors = new Dictionary<string, string[]>();
        var draft = ImportDraftJson.Deserialize(import.DraftJson!);
        if (request is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["A request body is required."] });
        }

        // A field that is absent (null) stays as it is; "" clears the title, description or a list of steps.
        var title = request.Title?.Trim() ?? draft.Title;
        if (title.Length > Recipe.MaxTitleLength || title.Any(char.IsControl))
        {
            errors["title"] = [$"The title can be at most {Recipe.MaxTitleLength} characters, without control characters."];
        }

        var description = request.Description is null ? draft.Description : string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is { Length: > Recipe.MaxDescriptionLength })
        {
            errors["description"] = [$"The description can be at most {Recipe.MaxDescriptionLength} characters."];
        }

        if (request.Servings is < Recipe.MinServings or > Recipe.MaxServings)
        {
            errors["servings"] = [$"Servings must be {Recipe.MinServings} to {Recipe.MaxServings}."];
        }

        CheckMinutes(request.PrepMinutes, "prepMinutes", errors);
        CheckMinutes(request.CookMinutes, "cookMinutes", errors);

        var steps = request.Steps ?? [.. draft.Steps];
        if (steps.Length > Recipe.MaxSteps)
        {
            errors["steps"] = [$"A recipe can have at most {Recipe.MaxSteps} steps."];
        }
        else
        {
            for (var i = 0; i < steps.Length; i++)
            {
                var step = steps[i]?.Trim();
                if (string.IsNullOrEmpty(step) || step.Length > RecipeStep.MaxTextLength)
                {
                    errors[$"steps[{i}]"] = [$"A step must be 1 to {RecipeStep.MaxTextLength} characters."];
                }
            }
        }

        var edits = request.Lines ?? [];
        if (edits.Length > Recipe.MaxIngredients)
        {
            errors["lines"] = [$"At most {Recipe.MaxIngredients} line edits."];
        }
        else
        {
            CheckEdits(edits, draft.Lines.Count, errors);
        }

        if (errors.Count == 0 && !await ResolveEditReferencesAsync(db, edits, errors, cancellationToken))
        {
            return Results.ValidationProblem(errors);
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var unitCodes = await db.Units.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Code, cancellationToken);
        var lines = draft.Lines.ToList();
        var removed = new HashSet<int>();
        foreach (var edit in edits)
        {
            var position = edit.Position!.Value;
            if (edit.Remove == true)
            {
                removed.Add(position);
                continue;
            }

            lines[position] = lines[position] with
            {
                IngredientId = edit.IngredientId,
                Quantity = edit.Quantity,
                QuantityLow = null,
                UnitId = edit.UnitId,
                UnitCode = edit.UnitId is { } unitId ? unitCodes.GetValueOrDefault(unitId) : null,
                UnitText = null,
                Note = string.IsNullOrWhiteSpace(edit.Note) ? null : edit.Note.Trim(),
                Confidence = 1,
                Unparsed = false,
                Suggestions = [],
            };
        }

        var updated = draft with
        {
            Title = title,
            Description = description,
            Servings = request.Servings ?? draft.Servings,
            PrepMinutes = request.PrepMinutes ?? draft.PrepMinutes,
            CookMinutes = request.CookMinutes ?? draft.CookMinutes,
            Steps = [.. steps.Select(s => s.Trim())],
            Lines = [.. lines.Where((_, index) => !removed.Contains(index))],
        };

        import.UpdateDraft(ImportDraftJson.Serialize(updated));
        if (await TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        return Results.Ok(await ToResponseAsync(db, import, updated, locale.Code, cancellationToken));
    }

    /// <summary>
    /// Creates the recipe from the draft through the recipe create path (<see cref="RecipeEndpoints.TryCreateAsync"/>: the same
    /// validation, and every id resolved through the filtered sets). Every line must be matched (structured, or to taste);
    /// otherwise the answer is a ValidationProblem on <c>lines[i].ingredientId</c> or <c>lines[i].unitId</c>. The recipe and the
    /// import's new status are written in one transaction, so saving twice at once yields one recipe (the row version).
    /// </summary>
    private static async Task<IResult> SaveImport(Guid householdId, Guid importId, AppDbContext db, CancellationToken cancellationToken)
    {
        var import = await FindAsync(db, importId, cancellationToken);
        if (import is null)
        {
            return NotFound();
        }

        if (import.Status != ImportStatus.Draft)
        {
            return NotDraft(import);
        }

        var draft = ImportDraftJson.Deserialize(import.DraftJson!);
        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < draft.Lines.Count; i++)
        {
            var line = draft.Lines[i];
            switch (line.Status)
            {
                case ImportLineStatus.Unparsed:
                case ImportLineStatus.UnmatchedIngredient:
                    errors[$"lines[{i}].ingredientId"] = [$"Choose an ingredient for '{Shorten(line.Original)}'."];
                    break;
                case ImportLineStatus.UnmatchedUnit:
                    errors[$"lines[{i}].unitId"] = [$"Choose a unit for '{Shorten(line.Original)}', or make it to taste."];
                    break;
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var request = new RecipeRequest(
            draft.Title,
            draft.Description,
            draft.Servings,
            draft.PrepMinutes,
            draft.CookMinutes,
            [.. draft.Steps.Select(s => new RecipeStepRequest(s))],
            [.. draft.Lines.Select(l => new RecipeIngredientRequest(l.IngredientId, l.Quantity, l.UnitId, l.Note))],
            null);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var recipe = await RecipeEndpoints.TryCreateAsync(db, request, errors, cancellationToken);
        if (recipe is null)
        {
            return Results.ValidationProblem(errors.ToDictionary(e => Rename(e.Key), e => e.Value));
        }

        import.MarkSaved(recipe.Id);
        if (await TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.Created($"/api/households/{householdId}/recipes/{recipe.Id}", new SaveRecipeImportResponse(recipe.Id));
    }

    private static async Task<IResult> DiscardImport(Guid importId, AppDbContext db, CancellationToken cancellationToken)
    {
        var import = await FindAsync(db, importId, cancellationToken);
        if (import is null)
        {
            return NotFound();
        }

        if (import.Status != ImportStatus.Draft)
        {
            return NotDraft(import);
        }

        import.Discard();
        if (await TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        return Results.NoContent();
    }

    /// <summary>The import in the route's household that has not been discarded (a discarded one is gone for the caller).</summary>
    private static Task<RecipeImport?> FindAsync(AppDbContext db, Guid importId, CancellationToken cancellationToken) =>
        db.RecipeImports.FirstOrDefaultAsync(i => i.Id == importId && i.Status != ImportStatus.Discarded, cancellationToken);

    private static async Task<IResult?> TrySaveAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.ImportConflict,
                "Import changed",
                "The import was changed or removed by someone else. Reload it and try again.");
        }
    }

    private static IResult NotFound() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.ImportNotFound, "Import not found");

    private static IResult NotDraft(RecipeImport import) =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.ImportNotDraft,
            "Import is not a draft",
            import.SavedRecipeId is { } recipeId ? $"The import was already saved as recipe {recipeId}." : "The import is no longer a draft.");

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..60] + "...";

    /// <summary>Recipe validation talks about <c>ingredients[i]</c>; the import's lines are <c>lines[i]</c> (same index).</summary>
    private static string Rename(string key) =>
        key.StartsWith("ingredients", StringComparison.Ordinal) ? "lines" + key["ingredients".Length..] : key;

    private static IResult FetchProblem(FetchResult result) => result.Failure switch
    {
        FetchFailure.InvalidUrl or FetchFailure.BlockedAddress => AppProblems.Problem(
            StatusCodes.Status400BadRequest,
            AppProblems.ImportUrlNotAllowed,
            "That address cannot be imported",
            "Only public web pages (http or https) can be imported. Paste the recipe text instead."),
        FetchFailure.TooManyRedirects => AppProblems.Problem(
            StatusCodes.Status422UnprocessableEntity, AppProblems.ImportFetchFailed, "The page could not be fetched", "The page redirected too many times."),
        FetchFailure.HttpStatus => AppProblems.Problem(
            StatusCodes.Status502BadGateway, AppProblems.ImportFetchFailed, "The page could not be fetched", $"The site answered with status {result.StatusCode}."),
        FetchFailure.UnsupportedContentType => AppProblems.Problem(
            StatusCodes.Status422UnprocessableEntity, AppProblems.ImportUnsupportedContent, "Not a web page", "The address is not an HTML page."),
        FetchFailure.TooLarge => AppProblems.Problem(
            StatusCodes.Status422UnprocessableEntity, AppProblems.ImportTooLarge, "The page is too large", "The page is too large to import. Paste the recipe text instead."),
        FetchFailure.Timeout => AppProblems.Problem(
            StatusCodes.Status504GatewayTimeout, AppProblems.ImportFetchTimeout, "The page took too long", "The site did not answer in time. Try again, or paste the recipe text."),
        _ => AppProblems.Problem(
            StatusCodes.Status502BadGateway, AppProblems.ImportFetchFailed, "The page could not be fetched", "The site could not be reached. Try again, or paste the recipe text."),
    };

    private static void CheckMinutes(int? minutes, string key, Dictionary<string, string[]> errors)
    {
        if (minutes is < 0 or > Recipe.MaxMinutes)
        {
            errors[key] = [$"Minutes must be 0 to {Recipe.MaxMinutes}."];
        }
    }

    private static void CheckEdits(UpdateImportLineRequest[] edits, int lineCount, Dictionary<string, string[]> errors)
    {
        var seen = new HashSet<int>();
        for (var i = 0; i < edits.Length; i++)
        {
            var key = $"lines[{i}]";
            var edit = edits[i];
            if (edit is null)
            {
                errors[key] = ["The line edit is required."];
                continue;
            }

            if (edit.Position is not { } position || position < 0 || position >= lineCount)
            {
                errors[$"{key}.position"] = [$"The position must be 0 to {lineCount - 1}."];
                continue;
            }

            if (!seen.Add(position))
            {
                errors[$"{key}.position"] = ["Each line can be edited once per request."];
                continue;
            }

            if (edit.Remove == true)
            {
                continue;
            }

            if (edit.IngredientId is null || edit.IngredientId == Guid.Empty)
            {
                errors[$"{key}.ingredientId"] = ["An ingredient is required."];
            }

            if (edit.Quantity is { } quantity)
            {
                if (quantity <= 0 || quantity > RecipeIngredient.MaxQuantity || decimal.Round(quantity, RecipeIngredient.MaxQuantityDecimals) != quantity)
                {
                    errors[$"{key}.quantity"] =
                        [$"The quantity must be greater than 0 and at most {RecipeIngredient.MaxQuantity:0}, with at most {RecipeIngredient.MaxQuantityDecimals} decimals."];
                }

                if (edit.UnitId is null || edit.UnitId == Guid.Empty)
                {
                    errors[$"{key}.unitId"] = ["A unit is required when there is a quantity."];
                }
            }
            else if (edit.UnitId is not null)
            {
                errors[$"{key}.unitId"] = ["Leave the unit out when there is no quantity (to taste)."];
            }

            if (edit.Note is { Length: > RecipeIngredient.MaxNoteLength })
            {
                errors[$"{key}.note"] = [$"The note can be at most {RecipeIngredient.MaxNoteLength} characters."];
            }
        }
    }

    /// <summary>
    /// Q-012 rule: every ingredient and unit id in the body is resolved through the filtered sets before anything is written.
    /// Another household's ingredient and an unknown one give the same 400 on that line.
    /// </summary>
    private static async Task<bool> ResolveEditReferencesAsync(
        AppDbContext db, UpdateImportLineRequest[] edits, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        var active = edits.Select((edit, index) => (edit, index)).Where(x => x.edit.Remove != true).ToList();
        var ingredientIds = active.Select(x => x.edit.IngredientId!.Value).Distinct().ToList();
        var unitIds = active.Where(x => x.edit.UnitId is not null).Select(x => x.edit.UnitId!.Value).Distinct().ToList();
        var knownIngredients = ingredientIds.Count == 0
            ? []
            : (await db.Ingredients.AsNoTracking().Where(i => ingredientIds.Contains(i.Id)).Select(i => i.Id).ToListAsync(cancellationToken)).ToHashSet();
        var knownUnits = unitIds.Count == 0
            ? []
            : (await db.Units.AsNoTracking().Where(u => unitIds.Contains(u.Id)).Select(u => u.Id).ToListAsync(cancellationToken)).ToHashSet();

        foreach (var (edit, index) in active)
        {
            if (!knownIngredients.Contains(edit.IngredientId!.Value))
            {
                errors[$"lines[{index}].ingredientId"] = ["The ingredient does not exist."];
            }

            if (edit.UnitId is { } unitId && !knownUnits.Contains(unitId))
            {
                errors[$"lines[{index}].unitId"] = ["The unit does not exist."];
            }
        }

        return errors.Count == 0;
    }

    /// <summary>
    /// The household's visible ingredients (global plus own, through the tenant filter, so never another household's) as match
    /// candidates, each with every name it has: the English and translated names of a global row, or the one typed name.
    /// </summary>
    private static async Task<IngredientMatcher> LoadMatcherAsync(AppDbContext db, string locale, CancellationToken cancellationToken)
    {
        var rows = await db.Ingredients.AsNoTracking().WithNames(db, CatalogTranslation.KindIngredient, locale).ToListAsync(cancellationToken);
        var translations = await db.CatalogTranslations.AsNoTracking().Where(t => t.Kind == CatalogTranslation.KindIngredient)
            .Select(t => new { t.EntityId, t.Name }).ToListAsync(cancellationToken);
        var namesById = translations.GroupBy(t => t.EntityId).ToDictionary(g => g.Key, g => g.Select(t => t.Name).ToList());

        return new IngredientMatcher(rows.Select(r =>
        {
            var own = r.Row.HouseholdId is not null;
            IReadOnlyList<string> names = own ? [r.Row.Name] : [r.Row.Name, .. namesById.GetValueOrDefault(r.Row.Id, [])];
            return new IngredientCandidate(r.Row.Id, r.Name, names, own);
        }));
    }

    private static async Task<Func<string, Guid?>> LoadUnitLookupAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var units = await db.Units.AsNoTracking().ToDictionaryAsync(u => u.Code, u => u.Id, cancellationToken);
        return code => units.TryGetValue(code, out var id) ? id : null;
    }

    private static async Task<RecipeImportResponse> ToResponseAsync(
        AppDbContext db, RecipeImport import, ImportDraft draft, string locale, CancellationToken cancellationToken)
    {
        var ingredientIds = draft.Lines.Where(l => l.IngredientId is not null).Select(l => l.IngredientId!.Value).Distinct().ToList();
        var names = ingredientIds.Count == 0
            ? []
            : await db.Ingredients.AsNoTracking().Where(i => ingredientIds.Contains(i.Id))
                .NamesByIdAsync(db, CatalogTranslation.KindIngredient, locale, cancellationToken);
        var units = (await db.Units.AsNoTracking().WithNames(db, CatalogTranslation.KindUnit, locale).ToListAsync(cancellationToken))
            .ToDictionary(u => u.Row.Id, u => (u.Row.Code, u.Name));

        var lines = draft.Lines.Select((line, index) =>
        {
            var unit = line.UnitId is { } unitId && units.TryGetValue(unitId, out var found) ? found : default;
            return new ImportLineResponse(
                index,
                line.Original,
                line.IngredientText,
                line.Quantity,
                line.QuantityLow,
                line.UnitText,
                line.UnitId,
                unit.Code ?? line.UnitCode,
                unit.Name,
                line.IngredientId,
                line.IngredientId is { } id ? names.GetValueOrDefault(id) : null,
                line.Confidence,
                line.Note,
                line.Status,
                [.. line.Suggestions.Select(s => new ImportSuggestionResponse(s.IngredientId, s.Name, s.Confidence))]);
        }).ToArray();

        return new RecipeImportResponse(
            import.Id,
            import.Source,
            import.SourceUrl,
            import.Status,
            draft.Title,
            draft.Description,
            draft.Servings,
            draft.PrepMinutes,
            draft.CookMinutes,
            draft.ImageUrl,
            [.. draft.Steps],
            lines,
            [.. draft.Warnings],
            draft.AllLinesMatched,
            import.SavedRecipeId,
            db.Entry(import).Property<DateTimeOffset>("CreatedAt").CurrentValue);
    }
}
