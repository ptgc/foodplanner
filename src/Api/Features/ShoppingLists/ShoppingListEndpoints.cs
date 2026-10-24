using System.Globalization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.MealPlans;
using Foodplanner.Domain.Quantities;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.ShoppingLists;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.ShoppingLists;

/// <summary>Add a manual item: free text plus an optional quantity (amount and unit go together).</summary>
public sealed record AddManualShoppingItemRequest(string? Label, decimal? Quantity, Guid? UnitId);

/// <summary>Set an item checked or unchecked. Set semantics: the same request twice is the same as once.</summary>
public sealed record CheckShoppingItemRequest(bool? Checked);

/// <summary>An amount in a unit; both null means "to taste".</summary>
public sealed record ShoppingQuantityResponse(decimal? Amount, string? UnitCode);

public sealed record ShoppingRecipeResponse(Guid Id, string Title);

/// <summary>
/// <c>Name</c> is the ingredient's name, or the label of a manual item. <c>Quantities</c> are display quantities (a
/// mass line and a volume line of one ingredient stay separate). <c>Recipes</c> are the recipes that contribute
/// (generated items only). <c>QuantityChangedSinceChecked</c>: checked, then a regeneration changed the quantities.
/// </summary>
public sealed record ShoppingListItemResponse(
    Guid Id,
    string Name,
    bool IsManual,
    Guid? IngredientId,
    ShoppingSection Section,
    ShoppingQuantityResponse[] Quantities,
    ShoppingRecipeResponse[] Recipes,
    bool Checked,
    Guid? CheckedByUserId,
    string? CheckedByEmail,
    DateTimeOffset? CheckedAt,
    bool QuantityChangedSinceChecked);

/// <summary><c>Id</c> is null when the week has no list yet (a read never creates one).</summary>
public sealed record ShoppingListResponse(
    Guid? Id, DateOnly WeekStart, ShoppingListItemResponse[] Main, ShoppingListItemResponse[] CheckYouHave);

/// <summary>
/// Weekly shopping lists (T-033), addressed by the ISO-8601 Monday of the meal plan's week. Every member, Child
/// included, reads, generates, adds and removes manual items and checks off (family use). See docs/domain.md for the
/// reconciliation, quantity-change and concurrency rules.
/// </summary>
public static class ShoppingListEndpoints
{
    private const int MaxAttempts = 5;
    private const int MaxManualItems = 200;

    private static readonly StringComparer DisplayComparer = CreateDisplayComparer();

    public static IEndpointRouteBuilder MapShoppingListEndpoints(this IEndpointRouteBuilder api)
    {
        var lists = api.MapHouseholdScopedGroup().MapGroup("/shopping-lists/{weekStart}");

        lists.MapGet("", GetList)
            .WithName("GetShoppingList")
            .Produces<ShoppingListResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        lists.MapPost("/generate", Generate)
            .WithName("GenerateShoppingList")
            .Produces<ShoppingListResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        lists.MapPost("/items", AddManualItem)
            .WithName("AddManualShoppingItem")
            .Produces<ShoppingListItemResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        lists.MapDelete("/items/{itemId:guid}", DeleteManualItem)
            .WithName("DeleteManualShoppingItem")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        lists.MapPut("/items/{itemId:guid}/check", CheckItem)
            .WithName("CheckShoppingItem")
            .Produces<ShoppingListItemResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    // ---- GET -------------------------------------------------------------------------------------------------------

    private static async Task<IResult> GetList(string weekStart, AppDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseWeekStart(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        var list = await db.ShoppingLists.AsNoTracking().FirstOrDefaultAsync(l => l.WeekStart == monday, cancellationToken);
        return Results.Ok(await BuildResponseAsync(db, list, monday, cancellationToken));
    }

    // ---- Generate / regenerate -------------------------------------------------------------------------------------

    private static async Task<IResult> Generate(
        Guid householdId, string weekStart, AppDbContext db, IShoppingListNotifier notifier, ILogger<ShoppingListChange> log,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseWeekStart(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        var generated = await GenerateFromPlanAsync(db, monday, cancellationToken);

        // Two members may regenerate at once, or someone may check an item while we flag it. Both surface as a
        // DbUpdateException (unique index, or a stale row version); the second pass reads the winner's rows and finds
        // little or nothing left to change, so the result is the same list.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ApplyAsync(db, monday, generated, cancellationToken);
                break;
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                db.ChangeTracker.Clear();
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                return Conflict();
            }
        }

        await NotifyAsync(notifier, log, new ShoppingListChange(householdId, monday, ShoppingListChangeKind.Generated, null), cancellationToken);
        var list = await db.ShoppingLists.AsNoTracking().FirstAsync(l => l.WeekStart == monday, cancellationToken);
        return Results.Ok(await BuildResponseAsync(db, list, monday, cancellationToken));
    }

    /// <summary>
    /// Maps the week's plan entries and their recipes onto the generator's input. Every read goes through the filtered
    /// sets: a recipe, an ingredient or a line of another household cannot appear. A "to taste" line has no unit in the
    /// recipe; the generator needs one, so it gets <c>stk</c>, which is dropped again when stored.
    /// </summary>
    private static async Task<GeneratedList> GenerateFromPlanAsync(AppDbContext db, DateOnly monday, CancellationToken cancellationToken)
    {
        var units = await db.Units.AsNoTracking().ToListAsync(cancellationToken);
        var catalog = new UnitCatalog(units);
        var unitsById = units.ToDictionary(u => u.Id);

        var entries = await (from plan in db.MealPlans.AsNoTracking()
                             join entry in db.MealPlanEntries.AsNoTracking() on plan.Id equals entry.MealPlanId
                             where plan.WeekStart == monday
                             orderby entry.Date, entry.Id
                             select new { entry.RecipeId, entry.Servings }).ToListAsync(cancellationToken);
        var recipeIds = entries.Select(e => e.RecipeId).Distinct().ToList();
        var recipes = await db.Recipes.AsNoTracking().Where(r => recipeIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Servings, cancellationToken);
        var lines = await db.RecipeIngredients.AsNoTracking().Where(l => recipeIds.Contains(l.RecipeId))
            .OrderBy(l => l.RecipeId).ThenBy(l => l.Position).ToListAsync(cancellationToken);
        var ingredientIds = lines.Select(l => l.IngredientId).Distinct().ToList();
        var ingredients = await db.Ingredients.AsNoTracking().Where(i => ingredientIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, cancellationToken);

        var toTasteUnit = catalog.Get("stk");
        var linesByRecipe = lines.ToLookup(l => l.RecipeId);
        var meals = new List<PlannedMeal>();
        foreach (var entry in entries)
        {
            if (!recipes.TryGetValue(entry.RecipeId, out var recipeServings))
            {
                continue;
            }

            var planned = new List<PlannedIngredient>();
            foreach (var line in linesByRecipe[entry.RecipeId])
            {
                if (!ingredients.TryGetValue(line.IngredientId, out var ingredient))
                {
                    continue;
                }

                Quantity quantity;
                if (line is { Quantity: { } amount, UnitId: { } unitId } && unitsById.TryGetValue(unitId, out var unit))
                {
                    quantity = new Quantity(amount, unit);
                }
                else
                {
                    quantity = new Quantity(null, toTasteUnit);
                }

                planned.Add(new PlannedIngredient(ingredient.Id, ingredient.Name, ingredient.IsStaple, quantity));
            }

            meals.Add(new PlannedMeal(entry.RecipeId, recipeServings, entry.Servings, planned));
        }

        return ShoppingListGenerator.Generate(meals, catalog);
    }

    /// <summary>One attempt: find or create the list, reconcile its generated items with <paramref name="generated"/>, save once.</summary>
    private static async Task ApplyAsync(AppDbContext db, DateOnly monday, GeneratedList generated, CancellationToken cancellationToken)
    {
        var list = await FindOrCreateListAsync(db, monday, cancellationToken);

        var items = await db.ShoppingListItems.Where(i => i.ShoppingListId == list.Id && i.IngredientId != null)
            .ToDictionaryAsync(i => i.Id, cancellationToken);
        var itemIds = items.Keys.ToList();
        var quantities = (await db.ShoppingListItemQuantities.Where(q => itemIds.Contains(q.ShoppingListItemId))
            .OrderBy(q => q.Position).ToListAsync(cancellationToken)).ToLookup(q => q.ShoppingListItemId);
        var sources = (await db.ShoppingListItemSources.Where(s => itemIds.Contains(s.ShoppingListItemId))
            .ToListAsync(cancellationToken)).ToLookup(s => s.ShoppingListItemId);

        var existing = items.Values.Select(i => new ExistingGeneratedItem(
            i.Id,
            i.IngredientId!.Value,
            i.Section,
            i.Checked,
            [.. quantities[i.Id].Select(q => new StoredQuantity(q.Amount, q.UnitId))],
            [.. sources[i.Id].Select(s => s.RecipeId)])).ToList();
        var plan = ShoppingListReconciler.Reconcile(existing, generated);

        foreach (var add in plan.Add)
        {
            var item = ShoppingListItem.Generated(list.Id, add.IngredientId, add.Section);
            db.ShoppingListItems.Add(item);
            AddQuantities(db, item.Id, add.Quantities);
            AddSources(db, item.Id, add.RecipeIds);
        }

        foreach (var update in plan.Update)
        {
            var item = items[update.ItemId];
            if (update.MoveToSection is { } section)
            {
                item.MoveTo(section);
            }

            if (update.Quantities is { } newQuantities)
            {
                db.ShoppingListItemQuantities.RemoveRange(quantities[item.Id]);
                AddQuantities(db, item.Id, newQuantities);
                if (update.FlagQuantityChange)
                {
                    item.QuantitiesChanged();
                }
            }

            if (update.RecipeIds is { } newRecipes)
            {
                db.ShoppingListItemSources.RemoveRange(sources[item.Id]);
                AddSources(db, item.Id, newRecipes);
            }
        }

        // The database removes the quantity and source rows with the item.
        db.ShoppingListItems.RemoveRange(plan.RemoveItemIds.Select(id => items[id]));

        await db.SaveChangesAsync(cancellationToken);
    }

    private static void AddQuantities(AppDbContext db, Guid itemId, IReadOnlyList<StoredQuantity> quantities)
    {
        for (var position = 0; position < quantities.Count; position++)
        {
            db.ShoppingListItemQuantities.Add(
                new ShoppingListItemQuantity(itemId, position, quantities[position].Amount, quantities[position].UnitId));
        }
    }

    private static void AddSources(AppDbContext db, Guid itemId, IEnumerable<Guid> recipeIds)
    {
        foreach (var recipeId in recipeIds.Distinct())
        {
            db.ShoppingListItemSources.Add(new ShoppingListItemSource(itemId, recipeId));
        }
    }

    /// <summary>The list of the week, created when missing. The unique (household, week) index settles two creators.</summary>
    private static async Task<ShoppingList> FindOrCreateListAsync(AppDbContext db, DateOnly monday, CancellationToken cancellationToken)
    {
        var list = await db.ShoppingLists.FirstOrDefaultAsync(l => l.WeekStart == monday, cancellationToken);
        if (list is not null)
        {
            return list;
        }

        list = new ShoppingList(monday);
        db.ShoppingLists.Add(list);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return list;
        }
        catch (DbUpdateException)
        {
            db.Entry(list).State = EntityState.Detached;
            return await db.ShoppingLists.FirstOrDefaultAsync(l => l.WeekStart == monday, cancellationToken)
                ?? throw new InvalidOperationException("The shopping list could not be created and does not exist.");
        }
    }

    // ---- Manual items ----------------------------------------------------------------------------------------------

    private static async Task<IResult> AddManualItem(
        Guid householdId, string weekStart, AddManualShoppingItemRequest? request, AppDbContext db,
        IShoppingListNotifier notifier, ILogger<ShoppingListChange> log, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseWeekStart(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        string? label = null;
        if (request is null)
        {
            errors["body"] = ["A request body is required."];
        }
        else
        {
            label = ShoppingListItem.NormalizeLabel(request.Label);
            if (label is null)
            {
                errors["label"] = [$"The label is required and at most {ShoppingListItem.MaxLabelLength} characters, without control characters."];
            }

            if ((request.Quantity is null) != (request.UnitId is null))
            {
                errors["quantity"] = ["Give a quantity and a unit together, or neither."];
            }
            else if (request.Quantity is { } q
                && (q <= 0 || q > RecipeIngredient.MaxQuantity || decimal.Round(q, RecipeIngredient.MaxQuantityDecimals) != q))
            {
                errors["quantity"] = [$"The quantity must be more than 0 and at most {RecipeIngredient.MaxQuantity:0}, with at most {RecipeIngredient.MaxQuantityDecimals} decimals."];
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // Q-012 rule: the unit is resolved before saving. Unknown ids get the same answer.
        if (request!.UnitId is { } unitId && !await db.Units.AnyAsync(u => u.Id == unitId, cancellationToken))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["unitId"] = ["The unit does not exist."] });
        }

        ShoppingListItem item;
        try
        {
            var list = await FindOrCreateListAsync(db, monday, cancellationToken);
            if (await db.ShoppingListItems.CountAsync(i => i.ShoppingListId == list.Id && i.IngredientId == null, cancellationToken) >= MaxManualItems)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["label"] = [$"A list holds at most {MaxManualItems} manual items."] });
            }

            item = ShoppingListItem.Manual(list.Id, label!);
            db.ShoppingListItems.Add(item);
            if (request.Quantity is { } amount)
            {
                db.ShoppingListItemQuantities.Add(new ShoppingListItemQuantity(item.Id, 0, amount, request.UnitId));
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Conflict();
        }

        await NotifyAsync(notifier, log, new ShoppingListChange(householdId, monday, ShoppingListChangeKind.ManualItemAdded, item.Id), cancellationToken);
        var created = (await LoadItemsAsync(db, item.ShoppingListId, item.Id, cancellationToken)).Single();
        return Results.Created($"/api/households/{householdId}/shopping-lists/{monday:yyyy-MM-dd}", created);
    }

    private static async Task<IResult> DeleteManualItem(
        Guid householdId, string weekStart, Guid itemId, AppDbContext db, IShoppingListNotifier notifier,
        ILogger<ShoppingListChange> log, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseWeekStart(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        var item = await FindItemAsync(db, monday, itemId, cancellationToken);
        if (item is null)
        {
            return ItemNotFound();
        }

        if (!item.IsManual)
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.ShoppingListItemGenerated,
                "Generated item",
                "Items made from the meal plan cannot be removed. Change the plan and regenerate the list.");
        }

        db.ShoppingListItems.Remove(item);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Already gone (someone else removed it, or the row changed under us): the item is deleted either way.
            db.ChangeTracker.Clear();
            if (await FindItemAsync(db, monday, itemId, cancellationToken) is not null)
            {
                return Conflict();
            }
        }

        await NotifyAsync(notifier, log, new ShoppingListChange(householdId, monday, ShoppingListChangeKind.ItemRemoved, itemId), cancellationToken);
        return Results.NoContent();
    }

    // ---- Check-off -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sets an item checked or unchecked with set semantics and an optimistic row version (see docs/domain.md): the item is
    /// read, changed in memory and saved; if someone else changed the row in between, the save is refused, the item is
    /// read again and the request is applied to the fresh state. So the same value twice (two members checking the same
    /// item) ends with the first writer as the checker and no error; opposite values end in whichever request commits
    /// last, with checker and time always from one writer. The response is the state after this request.
    /// </summary>
    private static async Task<IResult> CheckItem(
        Guid householdId, string weekStart, Guid itemId, CheckShoppingItemRequest? request, ICurrentUser user,
        TimeProvider time, AppDbContext db, IShoppingListNotifier notifier, ILogger<ShoppingListChange> log,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseWeekStart(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        if (request?.Checked is null)
        {
            errors["checked"] = ["checked (true or false) is required."];
        }

        if (errors.Count > 0 || user.UserId is not { } userId)
        {
            return Results.ValidationProblem(errors);
        }

        var wanted = request!.Checked!.Value;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var item = await FindItemAsync(db, monday, itemId, cancellationToken);
            if (item is null)
            {
                return ItemNotFound();
            }

            if (!item.SetChecked(wanted, userId, time.GetUtcNow()))
            {
                // Already in the wanted state (maybe because another member just did it): nothing to write.
                return Results.Ok((await LoadItemsAsync(db, item.ShoppingListId, item.Id, cancellationToken)).Single());
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                continue;
            }

            await NotifyAsync(
                notifier,
                log,
                new ShoppingListChange(householdId, monday, wanted ? ShoppingListChangeKind.ItemChecked : ShoppingListChangeKind.ItemUnchecked, itemId),
                cancellationToken);
            return Results.Ok((await LoadItemsAsync(db, item.ShoppingListId, item.Id, cancellationToken)).Single());
        }

        return Conflict();
    }

    // ---- Shared ----------------------------------------------------------------------------------------------------

    /// <summary>The week of the route: a <c>yyyy-MM-dd</c> date that is a Monday.</summary>
    private static bool TryParseWeekStart(string value, Dictionary<string, string[]> errors, out DateOnly weekStart)
    {
        if (!IsWeekStart(value, out weekStart))
        {
            errors["weekStart"] = ["The week must be the Monday of an ISO week, as yyyy-MM-dd."];
            return false;
        }

        return true;
    }

    internal static bool IsWeekStart(string value, out DateOnly weekStart) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out weekStart)
        && MealPlan.IsWeekStart(weekStart);

    private static Task<ShoppingListItem?> FindItemAsync(AppDbContext db, DateOnly monday, Guid itemId, CancellationToken cancellationToken) =>
        db.ShoppingListItems.FirstOrDefaultAsync(
            i => i.Id == itemId && db.ShoppingLists.Any(l => l.Id == i.ShoppingListId && l.WeekStart == monday), cancellationToken);

    /// <summary>The current list of a week as GET returns it (an empty shape when there is none). Used by the hub's GetState.</summary>
    internal static async Task<ShoppingListResponse> ReadAsync(AppDbContext db, DateOnly monday, CancellationToken cancellationToken)
    {
        var list = await db.ShoppingLists.AsNoTracking().FirstOrDefaultAsync(l => l.WeekStart == monday, cancellationToken);
        return await BuildResponseAsync(db, list, monday, cancellationToken);
    }

    private static async Task<ShoppingListResponse> BuildResponseAsync(
        AppDbContext db, ShoppingList? list, DateOnly monday, CancellationToken cancellationToken)
    {
        if (list is null)
        {
            return new ShoppingListResponse(null, monday, [], []);
        }

        var items = await LoadItemsAsync(db, list.Id, itemId: null, cancellationToken);
        return new ShoppingListResponse(
            list.Id,
            monday,
            [.. items.Where(i => i.Section == ShoppingSection.Main)],
            [.. items.Where(i => i.Section == ShoppingSection.CheckYouHave)]);
    }

    /// <summary>
    /// Items with display data, ordered by name (Norwegian culture comparison, so å sorts after z and æ ø å in that
    /// order) then id. Pass <paramref name="itemId"/> for one item.
    /// </summary>
    private static async Task<ShoppingListItemResponse[]> LoadItemsAsync(
        AppDbContext db, Guid listId, Guid? itemId, CancellationToken cancellationToken)
    {
        var items = await db.ShoppingListItems.AsNoTracking()
            .Where(i => i.ShoppingListId == listId && (itemId == null || i.Id == itemId)).ToListAsync(cancellationToken);
        var itemIds = items.Select(i => i.Id).ToList();

        var units = await db.Units.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Code, cancellationToken);
        var quantities = (await db.ShoppingListItemQuantities.AsNoTracking().Where(q => itemIds.Contains(q.ShoppingListItemId))
            .OrderBy(q => q.Position).ToListAsync(cancellationToken)).ToLookup(q => q.ShoppingListItemId);
        var sources = (await db.ShoppingListItemSources.AsNoTracking().Where(s => itemIds.Contains(s.ShoppingListItemId))
            .ToListAsync(cancellationToken)).ToLookup(s => s.ShoppingListItemId);

        var ingredientIds = items.Where(i => i.IngredientId != null).Select(i => i.IngredientId!.Value).Distinct().ToList();
        var ingredientNames = await db.Ingredients.AsNoTracking().Where(i => ingredientIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Name, cancellationToken);
        var recipeIds = sources.SelectMany(g => g).Select(s => s.RecipeId).Distinct().ToList();
        var recipeTitles = await db.Recipes.AsNoTracking().Where(r => recipeIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Title, cancellationToken);
        var userIds = items.Where(i => i.CheckedByUserId != null).Select(i => i.CheckedByUserId!.Value).Distinct().ToList();
        var emails = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken);

        return [.. items
            .Select(i => new ShoppingListItemResponse(
                i.Id,
                i.IngredientId is { } ingredientId ? ingredientNames.GetValueOrDefault(ingredientId, string.Empty) : i.Label ?? string.Empty,
                i.IsManual,
                i.IngredientId,
                i.Section,
                [.. quantities[i.Id].Select(q => new ShoppingQuantityResponse(q.Amount, q.UnitId is { } u ? units.GetValueOrDefault(u) : null))],
                [.. sources[i.Id]
                    .Where(s => recipeTitles.ContainsKey(s.RecipeId))
                    .Select(s => new ShoppingRecipeResponse(s.RecipeId, recipeTitles[s.RecipeId]))
                    .OrderBy(r => r.Title, DisplayComparer).ThenBy(r => r.Id)],
                i.Checked,
                i.CheckedByUserId,
                i.CheckedByUserId is { } by ? emails.GetValueOrDefault(by) : null,
                i.CheckedAt,
                i.QuantityChangedSinceChecked))
            .OrderBy(r => r.Name, DisplayComparer)
            .ThenBy(r => r.Id)];
    }

    /// <summary>Norwegian bokmål ordering when the runtime has the culture; ordinal ignoring case when it does not (invariant globalization).</summary>
    private static StringComparer CreateDisplayComparer()
    {
        try
        {
            return StringComparer.Create(CultureInfo.GetCultureInfo("nb-NO"), ignoreCase: true);
        }
        catch (CultureNotFoundException)
        {
            return StringComparer.OrdinalIgnoreCase;
        }
    }

    /// <summary>A listener failure never undoes a saved change.</summary>
    private static async Task NotifyAsync(
        IShoppingListNotifier notifier, ILogger<ShoppingListChange> log, ShoppingListChange change, CancellationToken cancellationToken)
    {
        try
        {
            await notifier.NotifyAsync(change, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Shopping list notifier failed for {Kind}", change.Kind);
        }
    }

    private static IResult ItemNotFound() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.ShoppingListItemNotFound, "Shopping list item not found");

    private static IResult Conflict() =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.ShoppingListConflict,
            "Shopping list changed",
            "The list was changed by someone else at the same time. Reload it and try again.");
}
