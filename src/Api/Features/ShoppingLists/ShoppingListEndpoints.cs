using System.Globalization;
using Foodplanner.Api.Features.Recipes;
using Foodplanner.Api.Localization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Localization;
using Foodplanner.Domain.MealPlans;
using Foodplanner.Domain.Quantities;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.ShoppingLists;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Localization;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.ShoppingLists;

/// <summary>Add a manual item: free text plus an optional quantity (amount and unit go together).</summary>
public sealed record AddManualShoppingItemRequest(string? Label, decimal? Quantity, Guid? UnitId);

/// <summary>Set an item checked or unchecked. Set semantics: the same request twice is the same as once.</summary>
public sealed record CheckShoppingItemRequest(bool? Checked);

/// <summary>An amount in a unit; both null means "to taste".</summary>
/// <summary><c>UnitCode</c> is the stable unit code; <c>UnitName</c> is the unit's name in the request language.</summary>
public sealed record ShoppingQuantityResponse(decimal? Amount, string? UnitCode, string? UnitName);

public sealed record ShoppingRecipeResponse(Guid Id, string Title);

/// <summary>
/// What the pantry held when the list was generated (T-093). <c>Quantity</c> null = "we have some", no amount known. On a main
/// item the item's <c>Quantities</c> are what is left to buy; in <c>CheckYouHave</c> they are what the plan needs.
/// </summary>
public sealed record ShoppingAtHomeResponse(ShoppingQuantityResponse? Quantity);

/// <summary>
/// <c>Section</c> is the part of the list (to buy or check you have); <c>StoreSection</c> (T-085) is the store section of the
/// ingredient (Other for a manual item) that the list is grouped and sorted by. <c>Name</c> is the ingredient's name, or the label of a manual item. <c>Quantities</c> are display quantities (a
/// mass line and a volume line of one ingredient stay separate). <c>Recipes</c> are the recipes that contribute
/// (generated items only). <c>QuantityChangedSinceChecked</c>: checked, then a regeneration changed the quantities.
/// <c>Alternatives</c> (T-076) are ingredients that can stand in ("or margarin"), from the contributing recipes' lines; shown by
/// the client as "or ...". A to taste, as needed or optional ingredient is in <c>CheckYouHave</c> with no quantity.
/// <c>IsWeekly</c> (T-092): the household's weekly groceries put (part of) this item on the list; it can be removed one by
/// one like a manual item (removing a weekly part leaves what the plan needs). <c>AtHome</c> (T-093): the pantry has some of it; null
/// when it does not.
/// </summary>
public sealed record ShoppingListItemResponse(
    Guid Id,
    string Name,
    bool IsManual,
    Guid? IngredientId,
    ShoppingSection Section,
    StoreSection StoreSection,
    ShoppingQuantityResponse[] Quantities,
    ShoppingRecipeResponse[] Recipes,
    bool Checked,
    Guid? CheckedByUserId,
    string? CheckedByEmail,
    DateTimeOffset? CheckedAt,
    bool QuantityChangedSinceChecked,
    RecipeAlternativeResponse[] Alternatives,
    bool IsWeekly,
    ShoppingAtHomeResponse? AtHome);

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

    private static readonly StringComparer NorwegianComparer = CreateDisplayComparer("nb-NO");
    private static readonly StringComparer EnglishComparer = CreateDisplayComparer("en-US");

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

        lists.MapPost("/weekly", AddWeeklyGroceries)
            .WithName("AddWeeklyGroceriesToShoppingList")
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

    private static async Task<IResult> GetList(string weekStart, RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseWeekStart(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        var list = await db.ShoppingLists.AsNoTracking().FirstOrDefaultAsync(l => l.WeekStart == monday, cancellationToken);
        return Results.Ok(await BuildResponseAsync(db, list, monday, locale.Code, cancellationToken));
    }

    // ---- Generate / regenerate -------------------------------------------------------------------------------------

    private static async Task<IResult> Generate(
        Guid householdId, string weekStart, RequestLocale locale, TimeProvider time, AppDbContext db, IShoppingListNotifier notifier,
        ILogger<ShoppingListChange> log, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseWeekStart(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        var generated = await GenerateFromPlanAsync(db, monday, cancellationToken);
        var now = time.GetUtcNow();

        // Two members may regenerate at once, or someone may check an item while we flag it. Both surface as a
        // DbUpdateException (unique index, or a stale row version); the second pass reads the winner's rows and finds
        // little or nothing left to change, so the result is the same list.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ApplyAsync(db, monday, generated, now, cancellationToken);
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
        return Results.Ok(await BuildResponseAsync(db, list, monday, locale.Code, cancellationToken));
    }

    /// <summary>
    /// Maps the week's plan entries and their recipes onto the generator's input. Every read goes through the filtered
    /// sets: a recipe, an ingredient or a line of another household cannot appear. A "to taste" line has no unit in the
    /// recipe; the generator needs one, so it gets <c>stk</c>, which is dropped again when stored.
    /// </summary>
    internal static async Task<GeneratedList> GenerateFromPlanAsync(AppDbContext db, DateOnly monday, CancellationToken cancellationToken)
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

                // Only a Measured line has an amount; ToTaste and AsNeeded carry the placeholder unit (T-076).
                Quantity quantity;
                var kind = line.AmountKind;
                if (kind == AmountKind.Measured && line is { Quantity: { } amount, UnitId: { } unitId } && unitsById.TryGetValue(unitId, out var unit))
                {
                    quantity = new Quantity(amount, unit);
                }
                else
                {
                    quantity = new Quantity(null, toTasteUnit);
                    kind = kind == AmountKind.Measured ? AmountKind.ToTaste : kind;
                }

                planned.Add(new PlannedIngredient(
                    ingredient.Id, ingredient.Name, ingredient.IsStaple, quantity, kind, line.Purpose, line.IsOptional));
            }

            meals.Add(new PlannedMeal(entry.RecipeId, recipeServings, entry.Servings, planned));
        }

        // The pantry (T-037, T-093) is subtracted in ApplyAsync, after the weekly groceries are laid over this list.
        return ShoppingListGenerator.Generate(meals, catalog);
    }

    /// <summary>One attempt: find or create the list, reconcile its generated items with <paramref name="generated"/>, save once.</summary>
    internal static async Task ApplyAsync(
        AppDbContext db, DateOnly monday, GeneratedList generated, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var list = await FindOrCreateListAsync(db, monday, cancellationToken);

        var items = await db.ShoppingListItems.Where(i => i.ShoppingListId == list.Id && i.IngredientId != null)
            .ToDictionaryAsync(i => i.Id, cancellationToken);

        // Weekly groceries (T-092): the weekly part of an item is laid over the plan's list, so regenerating keeps it.
        var units = await db.Units.AsNoTracking().ToListAsync(cancellationToken);
        var unitsById = units.ToDictionary(u => u.Id);
        generated = WeeklyGroceries.Overlay(
            generated,
            items.Values.Where(i => i.IsWeekly).Select(i => new WeeklyEntry(
                i.IngredientId!.Value,
                i.WeeklyAmount is { } amount && i.WeeklyUnitId is { } unitId && unitsById.TryGetValue(unitId, out var unit) ? new Quantity(amount, unit) : null)),
            new UnitCatalog(units));

        // The pantry (T-037, T-093): what the household has is subtracted from the whole need (plan plus weekly part). Pure filter.
        var pantry = await db.PantryItems.AsNoTracking().Select(p => new { p.IngredientId, p.ExpiresAt, p.Amount, p.UnitId })
            .ToListAsync(cancellationToken);
        generated = PantryFilter.Apply(
            generated,
            pantry.Select(p => new PantryEntry(
                p.IngredientId,
                p.ExpiresAt,
                p.Amount is { } have && p.UnitId is { } haveUnit && unitsById.TryGetValue(haveUnit, out var pantryUnit) ? new Quantity(have, pantryUnit) : null)),
            now,
            new UnitCatalog(units));
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
            [.. sources[i.Id].Select(s => s.RecipeId)],
            i.AtHome)).ToList();
        var plan = ShoppingListReconciler.Reconcile(existing, generated);

        foreach (var add in plan.Add)
        {
            var item = ShoppingListItem.Generated(list.Id, add.IngredientId, add.Section);
            db.ShoppingListItems.Add(item);
            AddQuantities(db, item.Id, add.Quantities);
            AddSources(db, item.Id, add.RecipeIds);
            item.SetAtHome(add.AtHome);
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

            if (update.AtHome is { } atHome)
            {
                item.SetAtHome(atHome.Value);
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
        Guid householdId, string weekStart, AddManualShoppingItemRequest? request, RequestLocale locale, AppDbContext db,
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
        var created = (await LoadItemsAsync(db, item.ShoppingListId, item.Id, locale.Code, cancellationToken)).Single();
        return Results.Created($"/api/households/{householdId}/shopping-lists/{monday:yyyy-MM-dd}", created);
    }

    private static async Task<IResult> DeleteManualItem(
        Guid householdId, string weekStart, Guid itemId, TimeProvider time, AppDbContext db, IShoppingListNotifier notifier,
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

        if (item.IsWeekly)
        {
            // T-092: only the weekly part goes. The list is then regenerated, which removes the item when the plan does not need
            // it and otherwise leaves the plan's amount.
            var removed = await RemoveWeeklyPartAsync(db, monday, itemId, time, cancellationToken);
            if (!removed)
            {
                return Conflict();
            }

            await NotifyAsync(notifier, log, new ShoppingListChange(householdId, monday, ShoppingListChangeKind.ItemRemoved, itemId), cancellationToken);
            return Results.NoContent();
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

    // ---- Weekly groceries (T-092) ----------------------------------------------------------------------------------

    /// <summary>
    /// The "Add weekly groceries" button. Every weekly grocery is made sure to be on the week's list (see
    /// <see cref="WeeklyGroceries.Plan"/>): missing ones are added, changed amounts replaced, the rest untouched, so pressing
    /// twice changes nothing and an item the user removed comes back. The list is then regenerated from the plan with the weekly
    /// parts laid over it (same code as the generate button), in one transaction. An empty set changes nothing.
    /// </summary>
    private static async Task<IResult> AddWeeklyGroceries(
        Guid householdId, string weekStart, RequestLocale locale, TimeProvider time, AppDbContext db, IShoppingListNotifier notifier,
        ILogger<ShoppingListChange> log, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParseWeekStart(weekStart, errors, out var monday))
        {
            return Results.ValidationProblem(errors);
        }

        var groceries = await db.WeeklyGroceries.AsNoTracking().Select(w => new WeeklyGrocerySpec(w.IngredientId, w.Amount, w.UnitId))
            .ToListAsync(cancellationToken);
        var changed = false;
        if (groceries.Count > 0)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                    var list = await FindOrCreateListAsync(db, monday, cancellationToken);
                    var items = await db.ShoppingListItems.Where(i => i.ShoppingListId == list.Id && i.IngredientId != null)
                        .ToListAsync(cancellationToken);
                    var plan = WeeklyGroceries.Plan(
                        groceries, items.Select(i => new WeeklyItemState(i.Id, i.IngredientId!.Value, i.IsWeekly, i.WeeklyAmount, i.WeeklyUnitId)));
                    if (!plan.IsEmpty)
                    {
                        var byId = items.ToDictionary(i => i.Id);
                        foreach (var update in plan.Update)
                        {
                            byId[update.ItemId].SetWeekly(update.Grocery.Amount, update.Grocery.UnitId);
                        }

                        foreach (var add in plan.Add)
                        {
                            db.ShoppingListItems.Add(ShoppingListItem.Weekly(list.Id, add.IngredientId, add.Amount, add.UnitId));
                        }

                        await db.SaveChangesAsync(cancellationToken);
                        await ApplyAsync(db, monday, await GenerateFromPlanAsync(db, monday, cancellationToken), time.GetUtcNow(), cancellationToken);
                        changed = true;
                    }

                    await transaction.CommitAsync(cancellationToken);
                    break;
                }
                catch (DbUpdateException) when (attempt < MaxAttempts)
                {
                    // Two members pressed at once (unique index) or a row changed: the second pass finds little left to do.
                    db.ChangeTracker.Clear();
                }
                catch (DbUpdateException)
                {
                    db.ChangeTracker.Clear();
                    return Conflict();
                }
            }
        }

        if (changed)
        {
            await NotifyAsync(notifier, log, new ShoppingListChange(householdId, monday, ShoppingListChangeKind.Generated, null), cancellationToken);
        }

        return Results.Ok(await ReadAsync(db, monday, locale.Code, cancellationToken));
    }

    /// <summary>Clears the weekly part of an item and regenerates the week (one transaction). False when it kept failing.</summary>
    private static async Task<bool> RemoveWeeklyPartAsync(
        AppDbContext db, DateOnly monday, Guid itemId, TimeProvider time, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var item = await FindItemAsync(db, monday, itemId, cancellationToken);
                if (item is null)
                {
                    return true; // removed by someone else in between: gone either way
                }

                if (item.ClearWeekly())
                {
                    await db.SaveChangesAsync(cancellationToken);
                    await ApplyAsync(db, monday, await GenerateFromPlanAsync(db, monday, cancellationToken), time.GetUtcNow(), cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }

        return false;
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
        Guid householdId, string weekStart, Guid itemId, CheckShoppingItemRequest? request, RequestLocale locale, ICurrentUser user,
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
                return Results.Ok((await LoadItemsAsync(db, item.ShoppingListId, item.Id, locale.Code, cancellationToken)).Single());
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
            return Results.Ok((await LoadItemsAsync(db, item.ShoppingListId, item.Id, locale.Code, cancellationToken)).Single());
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
    internal static async Task<ShoppingListResponse> ReadAsync(AppDbContext db, DateOnly monday, string locale, CancellationToken cancellationToken)
    {
        var list = await db.ShoppingLists.AsNoTracking().FirstOrDefaultAsync(l => l.WeekStart == monday, cancellationToken);
        return await BuildResponseAsync(db, list, monday, locale, cancellationToken);
    }

    private static async Task<ShoppingListResponse> BuildResponseAsync(
        AppDbContext db, ShoppingList? list, DateOnly monday, string locale, CancellationToken cancellationToken)
    {
        if (list is null)
        {
            return new ShoppingListResponse(null, monday, [], []);
        }

        var items = await LoadItemsAsync(db, list.Id, itemId: null, locale, cancellationToken);
        return new ShoppingListResponse(
            list.Id,
            monday,
            [.. items.Where(i => i.Section == ShoppingSection.Main)],
            [.. items.Where(i => i.Section == ShoppingSection.CheckYouHave)]);
    }

    /// <summary>
    /// Items with display data in the request language, ordered by store section (T-085, fixed walking order), then by name (Norwegian culture comparison, so å sorts after z and æ ø å in that
    /// order) then id. Generated items are stored by ingredient id, so the name is looked up per request: a language switch re-labels the list. Pass <paramref name="itemId"/> for one item.
    /// </summary>
    private static async Task<ShoppingListItemResponse[]> LoadItemsAsync(
        AppDbContext db, Guid listId, Guid? itemId, string locale, CancellationToken cancellationToken)
    {
        var items = await db.ShoppingListItems.AsNoTracking()
            .Where(i => i.ShoppingListId == listId && (itemId == null || i.Id == itemId)).ToListAsync(cancellationToken);
        var itemIds = items.Select(i => i.Id).ToList();

        var units = await db.Units.AsNoTracking().WithNames(db, CatalogTranslation.KindUnit, locale)
            .ToDictionaryAsync(u => u.Row.Id, u => (u.Row.Code, u.Name), cancellationToken);
        var quantities = (await db.ShoppingListItemQuantities.AsNoTracking().Where(q => itemIds.Contains(q.ShoppingListItemId))
            .OrderBy(q => q.Position).ToListAsync(cancellationToken)).ToLookup(q => q.ShoppingListItemId);
        var sources = (await db.ShoppingListItemSources.AsNoTracking().Where(s => itemIds.Contains(s.ShoppingListItemId))
            .ToListAsync(cancellationToken)).ToLookup(s => s.ShoppingListItemId);

        var ingredientIds = items.Where(i => i.IngredientId != null).Select(i => i.IngredientId!.Value).Distinct().ToList();
        var recipeIds = sources.SelectMany(g => g).Select(s => s.RecipeId).Distinct().ToList();

        // Alternatives (T-076) are not stored on the list: they are read from the contributing recipes' lines, so an item shows
        // "or margarin" for as long as a recipe it comes from says so.
        var recipeLines = ingredientIds.Count == 0 || recipeIds.Count == 0
            ? []
            : await db.RecipeIngredients.AsNoTracking().Where(l => recipeIds.Contains(l.RecipeId) && ingredientIds.Contains(l.IngredientId))
                .OrderBy(l => l.RecipeId).ThenBy(l => l.Position).Select(l => new { l.Id, l.RecipeId, l.IngredientId }).ToListAsync(cancellationToken);
        var lineIds = recipeLines.Select(l => l.Id).ToList();
        var alternativesByLine = lineIds.Count == 0
            ? Enumerable.Empty<Guid>().ToLookup(_ => Guid.Empty)
            : (await db.RecipeIngredientAlternatives.AsNoTracking().Where(a => lineIds.Contains(a.RecipeIngredientId))
                .OrderBy(a => a.Position).Select(a => new { a.RecipeIngredientId, a.IngredientId }).ToListAsync(cancellationToken))
                .ToLookup(a => a.RecipeIngredientId, a => a.IngredientId);
        var alternativeIdsByItem = items.Where(i => i.IngredientId != null).ToDictionary(
            i => i.Id,
            i =>
            {
                var itemRecipes = sources[i.Id].Select(s => s.RecipeId).ToHashSet();
                return ShoppingAlternatives.For(
                    i.IngredientId!.Value,
                    recipeLines.Where(l => l.IngredientId == i.IngredientId && itemRecipes.Contains(l.RecipeId)).Select(l => alternativesByLine[l.Id]));
            });
        var nameIds = ingredientIds.Concat(alternativeIdsByItem.Values.SelectMany(a => a)).Distinct().ToList();
        var ingredientNames = await db.Ingredients.AsNoTracking().Where(i => nameIds.Contains(i.Id))
            .NamesByIdAsync(db, CatalogTranslation.KindIngredient, locale, cancellationToken);
        var storeSections = ingredientIds.Count == 0
            ? []
            : await db.Ingredients.AsNoTracking().Where(i => ingredientIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Section, cancellationToken);
        var recipeTitles = await db.Recipes.AsNoTracking().Where(r => recipeIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Title, cancellationToken);
        var userIds = items.Where(i => i.CheckedByUserId != null).Select(i => i.CheckedByUserId!.Value).Distinct().ToList();
        var emails = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken);

        var comparer = locale == Locales.Norwegian ? NorwegianComparer : EnglishComparer;
        var responses = items
            .Select(i => new ShoppingListItemResponse(
                i.Id,
                i.IngredientId is { } ingredientId ? ingredientNames.GetValueOrDefault(ingredientId, string.Empty) : i.Label ?? string.Empty,
                i.IsManual,
                i.IngredientId,
                i.Section,
                i.IngredientId is { } sectionIngredient ? storeSections.GetValueOrDefault(sectionIngredient, StoreSection.Other) : StoreSection.Other,
                [.. quantities[i.Id].Select(q => new ShoppingQuantityResponse(
                    q.Amount, q.UnitId is { } u ? units.GetValueOrDefault(u).Code : null, q.UnitId is { } n ? units.GetValueOrDefault(n).Name : null))],
                [.. sources[i.Id]
                    .Where(s => recipeTitles.ContainsKey(s.RecipeId))
                    .Select(s => new ShoppingRecipeResponse(s.RecipeId, recipeTitles[s.RecipeId]))
                    .OrderBy(r => r.Title, comparer).ThenBy(r => r.Id)],
                i.Checked,
                i.CheckedByUserId,
                i.CheckedByUserId is { } by ? emails.GetValueOrDefault(by) : null,
                i.CheckedAt,
                i.QuantityChangedSinceChecked,
                [.. alternativeIdsByItem.GetValueOrDefault(i.Id, [])
                    .Select(id => new RecipeAlternativeResponse(id, ingredientNames.GetValueOrDefault(id, string.Empty)))],
                i.IsWeekly,
                i.IsAtHome
                    ? new ShoppingAtHomeResponse(i.AtHomeAmount is { } homeAmount
                        ? new ShoppingQuantityResponse(
                            homeAmount, i.AtHomeUnitId is { } hu ? units.GetValueOrDefault(hu).Code : null, i.AtHomeUnitId is { } hn ? units.GetValueOrDefault(hn).Name : null)
                        : null)
                    : null))
            .ToList();

        // Store section first (the fixed walking order, T-085), then name in the request language.
        return [.. StoreSectionOrder.Sort(responses, r => r.StoreSection, r => r.Name, comparer, r => r.Id)];
    }

    internal static StringComparer DisplayComparer(string locale) => locale == Locales.Norwegian ? NorwegianComparer : EnglishComparer;

    /// <summary>The request language's ordering (nb-NO or en-US) when the runtime has the culture; ordinal ignoring case when it does not (invariant globalization).</summary>
    private static StringComparer CreateDisplayComparer(string culture)
    {
        try
        {
            return StringComparer.Create(CultureInfo.GetCultureInfo(culture), ignoreCase: true);
        }
        catch (CultureNotFoundException)
        {
            return StringComparer.OrdinalIgnoreCase;
        }
    }

    /// <summary>A listener failure never undoes a saved change.</summary>
    internal static async Task NotifyAsync(
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
