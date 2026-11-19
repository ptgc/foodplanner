using Foodplanner.Api.Authorization;
using Foodplanner.Api.Features.MealPlans;
using Foodplanner.Api.Features.ShoppingLists;
using Foodplanner.Api.Localization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Images;
using Foodplanner.Domain.MealPlans;
using Foodplanner.Domain.Quantities;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.ShoppingLists;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Localization;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Recipes;

/// <summary>
/// One thing a suggested recipe lacks. <c>Quantities</c> is what is missing (the whole need, or the shortfall) in the request
/// language's units; <c>Reason</c> is NotAtHome, NotEnough or UnitMismatch; <c>Alternatives</c> are other ingredients that would do.
/// </summary>
public sealed record MissingIngredientResponse(
    Guid IngredientId, string Name, ShoppingQuantityResponse[] Quantities, MissingReason Reason, RecipeAlternativeResponse[] Alternatives);

/// <summary>
/// A recipe the household can (nearly) cook from the pantry (T-100). <c>Servings</c> are the servings the match was made for.
/// <c>Missing</c> has at most 2 entries (empty = can cook now). <c>KidLikes</c> is how many kid profiles like it;
/// <c>CookedRecently</c>: it was on a plan in the last 14 days; <c>PlannedToday</c>: it is on today's plan already.
/// </summary>
public sealed record RecipeSuggestionResponse(
    Guid RecipeId,
    string Title,
    int Servings,
    int? TotalMinutes,
    FoodType FoodType,
    string? ThumbUrl,
    string? CardUrl,
    int KidLikes,
    bool CookedRecently,
    bool PlannedToday,
    MissingIngredientResponse[] Missing);

/// <summary><c>Today</c> is the date used (Europe/Oslo); <c>WeekStart</c> its ISO Monday.</summary>
public sealed record RecipeSuggestionsResponse(DateOnly Today, DateOnly WeekStart, RecipeSuggestionResponse[] Items);

/// <summary>Cook a suggestion today. <c>Servings</c> defaults to the recipe's servings (at most 50).</summary>
public sealed record CookTodayRequest(int? Servings);

/// <summary><c>AlreadyPlanned</c>: the recipe was on today's plan already, so nothing was added and <c>Entry</c> is the existing one.</summary>
public sealed record CookTodayResponse(MealPlanEntryResponse Entry, DateOnly WeekStart, bool AlreadyPlanned);

/// <summary>What was missing at the planned servings; the week's shopping list was regenerated so it now holds it.</summary>
public sealed record AddMissingResponse(DateOnly WeekStart, MissingIngredientResponse[] Missing);

/// <summary>
/// "What can I make with what we have?" (T-100, Q-029). Reading and both actions are for a full session (not kid mode: the screen is
/// hidden there). Reading: every member outside kid mode, a Child included (the group default <c>HouseholdMember</c>; it only
/// shows recipes and the pantry, which a Child can read anyway). The two actions are Adult or Owner (<c>HouseholdAdult</c>)
/// plus <c>FullSession</c>, so kid mode answers <c>403 kid-mode</c> and a Child member <c>403 forbidden</c>. See docs/domain.md.
/// </summary>
public static class RecipeSuggestionEndpoints
{
    public static IEndpointRouteBuilder MapRecipeSuggestionEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapHouseholdScopedGroup().MapGroup("/what-can-i-make");

        group.MapGet("", GetSuggestions)
            .WithName("GetRecipeSuggestionsFromPantry")
            .RequireAuthorization(Policies.FullSession)
            .Produces<RecipeSuggestionsResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{recipeId:guid}/cook-today", CookToday)
            .WithName("CookRecipeToday")
            .RequireAuthorization(Policies.HouseholdAdult)
            .RequireAuthorization(Policies.FullSession)
            .Produces<CookTodayResponse>()
            .Produces<CookTodayResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{recipeId:guid}/add-missing", AddMissing)
            .WithName("AddMissingIngredientsToShoppingList")
            .RequireAuthorization(Policies.HouseholdAdult)
            .RequireAuthorization(Policies.FullSession)
            .Produces<AddMissingResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static async Task<IResult> GetSuggestions(
        Guid householdId, RequestLocale locale, TimeProvider time, AppDbContext db, CancellationToken cancellationToken, int? servings = null)
    {
        if (ServingsError(servings) is { } error)
        {
            return error;
        }

        var today = GroceryCalendar.Today(time);
        var data = await LoadAsync(db, only: null, today, cancellationToken);
        var matches = PantryRecipeMatcher.Suggest(data.Candidates, data.Pantry, time.GetUtcNow(), today, data.Catalog, servings, data.Densities);

        var ids = matches.SelectMany(m => m.Missing.SelectMany(x => x.AlternativeIds.Append(x.IngredientId))).Distinct().ToList();
        var names = await IngredientNamesAsync(db, ids, locale.Code, cancellationToken);
        var unitNames = await UnitNamesAsync(db, locale.Code, cancellationToken);
        var plannedToday = await db.MealPlanEntries.AsNoTracking().Where(e => e.Date == today).Select(e => e.RecipeId).Distinct().ToListAsync(cancellationToken);

        var items = matches.Select(m =>
        {
            var recipe = data.Recipes[m.Recipe.RecipeId];
            return new RecipeSuggestionResponse(
                m.Recipe.RecipeId,
                m.Recipe.Title,
                m.Servings,
                recipe.TotalMinutes,
                m.Recipe.FoodType,
                RecipeImageUrls.For(householdId, recipe.Id, recipe.ImageVersion, RecipeImageSize.Thumb),
                RecipeImageUrls.For(householdId, recipe.Id, recipe.ImageVersion, RecipeImageSize.Card),
                m.Recipe.KidLikes,
                PantryRecipeMatcher.IsRecent(m.Recipe, today),
                plannedToday.Contains(m.Recipe.RecipeId),
                ToResponses(m, names, unitNames));
        }).ToArray();
        return Results.Ok(new RecipeSuggestionsResponse(today, GrocerySchedule.WeekStartOf(today), items));
    }

    /// <summary>
    /// Adds the recipe to TODAY's plan (the ISO week of today in Europe/Oslo). The planner's rules for a day apply: a day can hold
    /// several meals and an Adult or Owner is not held to the meal mix or the week lock, so a recipe is simply added next to what
    /// today already has. The one exception is the same recipe: if it is on today's plan already nothing is added (<c>200</c>,
    /// <c>alreadyPlanned</c>), so a double tap or two members tapping at once never plan it twice. The plan row is locked for the
    /// check and the insert (the planner's lock pattern), and the usual <c>MealPlanChanged</c> signal is sent.
    /// </summary>
    private static async Task<IResult> CookToday(
        Guid householdId, Guid recipeId, CookTodayRequest? request, ICurrentUser user, TimeProvider time, AppDbContext db,
        IMealPlanWeekAccess week, IMealPlanNotifier notifier, ILogger<MealPlan> logger, CancellationToken cancellationToken)
    {
        if (ServingsError(request?.Servings) is { } error)
        {
            return error;
        }

        if (user.UserId is not { } userId)
        {
            return AppProblems.Problem(StatusCodes.Status401Unauthorized, AppProblems.Unauthenticated, "Not signed in");
        }

        var recipe = await db.Recipes.AsNoTracking().Where(r => r.Id == recipeId).Select(r => new { r.Id, r.Servings }).FirstOrDefaultAsync(cancellationToken);
        if (recipe is null)
        {
            return RecipeNotFound();
        }

        var today = GroceryCalendar.Today(time);
        var monday = GrocerySchedule.WeekStartOf(today);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var plan = await MealPlanEndpoints.FindOrCreatePlanAsync(db, monday, cancellationToken);
        await MealPlanEndpoints.LockPlanRowAsync(db, plan, cancellationToken);
        if (!await week.CanWriteAsync(plan))
        {
            return MealPlanEndpoints.WeekLocked();
        }

        var existing = await db.MealPlanEntries.AsNoTracking()
            .Where(e => e.MealPlanId == plan.Id && e.Date == today && e.RecipeId == recipeId)
            .OrderBy(e => e.Id).Select(e => e.Id).FirstOrDefaultAsync(cancellationToken);
        if (existing != Guid.Empty)
        {
            await transaction.CommitAsync(cancellationToken);
            var current = (await MealPlanEndpoints.LoadEntriesAsync(db, plan.Id, existing, cancellationToken)).Single();
            return Results.Ok(new CookTodayResponse(current, monday, AlreadyPlanned: true));
        }

        var servings = request?.Servings ?? Math.Min(recipe.Servings, MealPlanEntry.MaxServings);
        var entry = new MealPlanEntry(plan.Id, monday, today, recipe.Id, servings, userId);
        db.MealPlanEntries.Add(entry);
        if (await MealPlanEndpoints.TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(cancellationToken);
        await MealPlanEndpoints.NotifyAsync(notifier, logger, new MealPlanChange(householdId, monday, MealPlanChangeKind.EntryAdded, entry.Id), cancellationToken);
        var created = (await MealPlanEndpoints.LoadEntriesAsync(db, plan.Id, entry.Id, cancellationToken)).Single();
        return Results.Created($"/api/households/{householdId}/meal-plans/{monday:yyyy-MM-dd}", new CookTodayResponse(created, monday, AlreadyPlanned: false));
    }

    /// <summary>
    /// Puts the missing items on this week's shopping list. The list is generated from the plan, so the recipe must be on this week's
    /// plan (<c>409 recipe-not-planned</c> otherwise: use "cook today" first). It is computed at the planned servings (today's entry,
    /// else the earliest this week), and the week's list is regenerated exactly like <c>POST shopping-lists/{week}/generate</c> (same
    /// list lock, retry and <c>Generated</c> signal): the pantry is subtracted there, so what lands on the list is the missing
    /// amount, merged with what other planned recipes and weekly groceries need and with existing lines by the list's own rules.
    /// Nothing missing changes nothing. Idempotent. The response lists what was missing.
    /// </summary>
    private static async Task<IResult> AddMissing(
        Guid householdId, Guid recipeId, RequestLocale locale, TimeProvider time, AppDbContext db, IShoppingListNotifier notifier,
        ILogger<ShoppingListChange> log, CancellationToken cancellationToken)
    {
        var today = GroceryCalendar.Today(time);
        var monday = GrocerySchedule.WeekStartOf(today);
        var data = await LoadAsync(db, [recipeId], today, cancellationToken);
        if (data.Candidates.Count == 0)
        {
            return RecipeNotFound();
        }

        var planned = await (from entry in db.MealPlanEntries.AsNoTracking()
                             join plan in db.MealPlans.AsNoTracking() on entry.MealPlanId equals plan.Id
                             where plan.WeekStart == monday && entry.RecipeId == recipeId
                             orderby entry.Date == today descending, entry.Date, entry.Id
                             select new { entry.Servings }).FirstOrDefaultAsync(cancellationToken);
        if (planned is null)
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.RecipeNotPlanned,
                "The recipe is not on this week's plan",
                "Add the recipe to the plan first (cook it today); the shopping list is made from the plan.");
        }

        var match = PantryRecipeMatcher.Match(data.Candidates[0], data.Pantry, time.GetUtcNow(), data.Catalog, planned.Servings, data.Densities);
        if (match.MissingCount > 0)
        {
            if (!await ShoppingListEndpoints.RegenerateFromPlanAsync(db, monday, time.GetUtcNow(), cancellationToken))
            {
                return ShoppingListEndpoints.Conflict();
            }

            await ShoppingListEndpoints.NotifyAsync(
                notifier, log, new ShoppingListChange(householdId, monday, ShoppingListChangeKind.Generated, null), cancellationToken);
        }

        var names = await IngredientNamesAsync(
            db, [.. match.Missing.SelectMany(x => x.AlternativeIds.Append(x.IngredientId)).Distinct()], locale.Code, cancellationToken);
        return Results.Ok(new AddMissingResponse(monday, ToResponses(match, names, await UnitNamesAsync(db, locale.Code, cancellationToken))));
    }

    private static IResult? ServingsError(int? servings) =>
        servings is < MealPlanEntry.MinServings or > MealPlanEntry.MaxServings
            ? Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["servings"] = [$"Servings must be {MealPlanEntry.MinServings} to {MealPlanEntry.MaxServings}."],
            })
            : null;

    private static IResult RecipeNotFound() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.RecipeNotFound, "Recipe not found");

    private static MissingIngredientResponse[] ToResponses(
        RecipeMatch match, Dictionary<Guid, string> names, Dictionary<string, string> unitNames) =>
        [.. match.Missing
            .Select(m => new MissingIngredientResponse(
                m.IngredientId,
                names.GetValueOrDefault(m.IngredientId, string.Empty),
                [.. m.Quantities.Select(q => new ShoppingQuantityResponse(q.Amount, q.Unit.Code, unitNames.GetValueOrDefault(q.Unit.Code, q.Unit.Name)))],
                m.Reason,
                [.. m.AlternativeIds.Select(a => new RecipeAlternativeResponse(a, names.GetValueOrDefault(a, string.Empty)))]))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.IngredientId)];

    private static Task<Dictionary<Guid, string>> IngredientNamesAsync(
        AppDbContext db, List<Guid> ids, string locale, CancellationToken cancellationToken) =>
        db.Ingredients.AsNoTracking().Where(i => ids.Contains(i.Id)).NamesByIdAsync(db, CatalogTranslation.KindIngredient, locale, cancellationToken);

    private static async Task<Dictionary<string, string>> UnitNamesAsync(AppDbContext db, string locale, CancellationToken cancellationToken) =>
        await db.Units.AsNoTracking().WithNames(db, CatalogTranslation.KindUnit, locale)
            .ToDictionaryAsync(u => u.Row.Code, u => u.Name, cancellationToken);

    private sealed record Loaded(
        List<RecipeCandidate> Candidates,
        Dictionary<Guid, Recipe> Recipes,
        List<PantryEntry> Pantry,
        UnitCatalog Catalog,
        IReadOnlyDictionary<Guid, decimal>? Densities);

    /// <summary>
    /// The household's recipes (or just <paramref name="only"/>) as matcher candidates, and its pantry. Every read goes through the
    /// filtered sets, so another household's recipes, ingredients, likes, plans and pantry cannot appear. Fixed number of queries.
    /// </summary>
    private static async Task<Loaded> LoadAsync(AppDbContext db, Guid[]? only, DateOnly today, CancellationToken cancellationToken)
    {
        var units = await db.Units.AsNoTracking().ToListAsync(cancellationToken);
        var unitsById = units.ToDictionary(u => u.Id);
        var recipes = await db.Recipes.AsNoTracking().Where(r => only == null || only.Contains(r.Id)).ToListAsync(cancellationToken);
        var ids = recipes.Select(r => r.Id).ToList();
        var lines = await db.RecipeIngredients.AsNoTracking().Where(l => only == null || ids.Contains(l.RecipeId))
            .OrderBy(l => l.RecipeId).ThenBy(l => l.Position).ToListAsync(cancellationToken);
        var alternatives = (await db.RecipeIngredientAlternatives.AsNoTracking().OrderBy(a => a.Position).ToListAsync(cancellationToken))
            .ToLookup(a => a.RecipeIngredientId);
        var staples = (await db.Ingredients.AsNoTracking().Where(i => i.IsStaple).Select(i => i.Id).ToListAsync(cancellationToken)).ToHashSet();
        var likes = await db.RecipeLikes.AsNoTracking().GroupBy(l => l.RecipeId).Select(g => new { RecipeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RecipeId, x => x.Count, cancellationToken);
        var from = today.AddDays(-PantryRecipeMatcher.RecentDays);
        var last = await db.MealPlanEntries.AsNoTracking().Where(e => e.Date >= from && e.Date <= today)
            .GroupBy(e => e.RecipeId).Select(g => new { RecipeId = g.Key, Last = g.Max(e => e.Date) })
            .ToDictionaryAsync(x => x.RecipeId, x => x.Last, cancellationToken);

        var linesByRecipe = lines.ToLookup(l => l.RecipeId);
        var candidates = new List<RecipeCandidate>();
        foreach (var recipe in recipes)
        {
            var matchLines = new List<RecipeLine>();
            foreach (var line in linesByRecipe[recipe.Id])
            {
                var options = new List<LineOption> { new(line.IngredientId, staples.Contains(line.IngredientId)) };
                options.AddRange(alternatives[line.Id].Select(a => new LineOption(a.IngredientId, staples.Contains(a.IngredientId))));

                Quantity? quantity = null;
                var kind = line.AmountKind;
                if (kind == AmountKind.Measured)
                {
                    if (line is { Quantity: { } amount, UnitId: { } unitId } && unitsById.TryGetValue(unitId, out var unit))
                    {
                        quantity = new Quantity(amount, unit);
                    }
                    else
                    {
                        kind = AmountKind.ToTaste; // a unit that cannot be read: treated like the shopping list does, never missing
                    }
                }

                matchLines.Add(new RecipeLine(options, kind, quantity, line.Purpose, line.IsOptional));
            }

            candidates.Add(new RecipeCandidate(
                recipe.Id, recipe.Title, recipe.FoodType, recipe.Servings, matchLines, likes.GetValueOrDefault(recipe.Id),
                last.TryGetValue(recipe.Id, out var date) ? date : null));
        }

        var pantry = new List<PantryEntry>();
        foreach (var item in await db.PantryItems.AsNoTracking().ToListAsync(cancellationToken))
        {
            var amount = item.Amount is { } a && item.UnitId is { } u && unitsById.TryGetValue(u, out var unit) ? new Quantity(a, unit) : null;
            pantry.Add(new PantryEntry(item.IngredientId, item.ExpiresAt, amount));
        }

        // The model has no density per ingredient yet, so mass and volume never convert (docs/domain.md, "What can I make (T-100)").
        return new Loaded(
            [.. candidates.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.RecipeId)],
            recipes.ToDictionary(r => r.Id),
            pantry,
            new UnitCatalog(units),
            Densities: null);
    }
}
