using Foodplanner.Api.Authorization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.ShoppingLists;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.ShoppingLists;

/// <summary>
/// Create or change a store. <c>SectionOrder</c> names every store section exactly once (Other included), as the strings of the
/// <c>StoreSection</c> enum; on create it may be left out (the fixed order), on change it replaces the order.
/// </summary>
public sealed record StoreRequest(string? Name, string[]? SectionOrder);

public sealed record StoreResponse(Guid Id, string Name, StoreSection[] SectionOrder);

/// <summary>Pick the store being shopped at; <c>StoreId</c> null (or left out) goes back to the fixed order.</summary>
public sealed record ChooseStoreRequest(Guid? StoreId);

/// <summary>The store the caller shops at in this household; null = the fixed order.</summary>
public sealed record ChosenStoreResponse(Guid? StoreId);

/// <summary>
/// The household's stores, each with a manual section order (T-087, Q-019). Any member reads the stores and picks the one they
/// shop at (family use, group default <c>HouseholdMember</c>); Adult or Owner create, change and delete them (policy
/// <c>HouseholdAdult</c>). The choice is stored per user and household (<c>Membership.ChosenStoreId</c>), so it follows the user
/// across devices, and the shopping list is ordered for each viewer by their own choice. See docs/domain.md.
/// </summary>
public static class StoreEndpoints
{
    public static IEndpointRouteBuilder MapStoreEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapHouseholdScopedGroup();
        var stores = group.MapGroup("/stores");

        stores.MapGet("", ListStores)
            .WithName("ListStores")
            .Produces<StoreResponse[]>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        stores.MapPost("", CreateStore)
            .WithName("CreateStore")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces<StoreResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        stores.MapPut("/{id:guid}", UpdateStore)
            .WithName("UpdateStore")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces<StoreResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        stores.MapDelete("/{id:guid}", DeleteStore)
            .WithName("DeleteStore")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var chosen = group.MapGroup("/chosen-store");

        chosen.MapGet("", GetChosenStore)
            .WithName("GetChosenStore")
            .Produces<ChosenStoreResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        chosen.MapPut("", ChooseStore)
            .WithName("ChooseStore")
            .Produces<ChosenStoreResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    /// <summary>
    /// The section order of the store the user has chosen in the current household, or null for the fixed order (no choice,
    /// no user, or a store that is gone). Used to order the shopping list for the viewer.
    /// </summary>
    internal static async Task<IReadOnlyList<StoreSection>?> ChosenOrderAsync(AppDbContext db, Guid? userId, CancellationToken cancellationToken)
    {
        if (userId is not { } id)
        {
            return null;
        }

        var storeId = await db.Memberships.AsNoTracking().Where(m => m.UserId == id).Select(m => m.ChosenStoreId).FirstOrDefaultAsync(cancellationToken);
        if (storeId is not { } chosen)
        {
            return null;
        }

        var text = await db.Stores.AsNoTracking().Where(s => s.Id == chosen).Select(s => s.SectionOrderText).FirstOrDefaultAsync(cancellationToken);
        if (text is null)
        {
            return null;
        }

        return StoreSectionOrder.Effective([.. text.Split(',').Select(n => StoreSections.TryParse(n, out var s) ? s : StoreSection.Other)]);
    }

    private static StoreResponse ToResponse(Store s) => new(s.Id, s.Name, [.. s.SectionOrder]);

    private static async Task<IResult> ListStores(AppDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Stores.AsNoTracking().ToListAsync(cancellationToken);
        return Results.Ok(rows.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id).Select(ToResponse).ToArray());
    }

    private static async Task<IResult> CreateStore(StoreRequest? request, AppDbContext db, CancellationToken cancellationToken)
    {
        if (!TryValidate(request, orderRequired: false, out var name, out var order, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        if (await db.Stores.AnyAsync(s => s.Name == name, cancellationToken))
        {
            return Duplicate(name);
        }

        if (await db.Stores.CountAsync(cancellationToken) >= Store.MaxPerHousehold)
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.StoreLimit,
                "Too many stores",
                $"A household can have at most {Store.MaxPerHousehold} stores.");
        }

        var store = new Store(name, order);
        db.Stores.Add(store);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            return Duplicate(name);
        }

        return Results.Created((string?)null, ToResponse(store));
    }

    private static async Task<IResult> UpdateStore(Guid id, StoreRequest? request, AppDbContext db, CancellationToken cancellationToken)
    {
        if (!TryValidate(request, orderRequired: true, out var name, out var order, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (store is null)
        {
            return AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.StoreNotFound, "Store not found");
        }

        if (await db.Stores.AnyAsync(s => s.Id != id && s.Name == name, cancellationToken))
        {
            return Duplicate(name);
        }

        store.Rename(name);
        store.SetSectionOrder(order!);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            return Duplicate(name);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.StoreNotFound, "Store not found");
        }

        return Results.Ok(ToResponse(store));
    }

    /// <summary>Idempotent. Every user's choice of this store is cleared first (they fall back to the fixed order).</summary>
    private static async Task<IResult> DeleteStore(Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        // Two passes: a member may choose the store between loading the memberships and the commit (foreign key violation);
        // the second pass reloads and clears them again.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (store is null)
            {
                return Results.NoContent();
            }

            foreach (var membership in await db.Memberships.Where(m => m.ChosenStoreId == id).ToListAsync(cancellationToken))
            {
                membership.ChooseStore(null);
            }

            db.Stores.Remove(store);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear(); // removed by someone else in between: gone either way
                return Results.NoContent();
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }

        return AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.StoreConflict,
            "Store could not be deleted",
            "Someone chose the store while it was being deleted. Try again.");
    }

    private static async Task<IResult> GetChosenStore(ICurrentUser user, AppDbContext db, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return Results.Unauthorized();
        }

        var storeId = await db.Memberships.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.ChosenStoreId).FirstOrDefaultAsync(cancellationToken);
        return Results.Ok(new ChosenStoreResponse(storeId));
    }

    private static async Task<IResult> ChooseStore(ChooseStoreRequest? request, ICurrentUser user, AppDbContext db, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return Results.Unauthorized();
        }

        var storeId = request?.StoreId;
        if (storeId == Guid.Empty)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["storeId"] = ["The store does not exist."] });
        }

        // Q-012 rule: the store is resolved through the filtered set. Another household's id gets the same answer as an unknown one.
        if (storeId is { } wanted && !await db.Stores.AnyAsync(s => s.Id == wanted, cancellationToken))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["storeId"] = ["The store does not exist."] });
        }

        var membership = await db.Memberships.FirstOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        if (membership is null)
        {
            return AppProblems.Problem(StatusCodes.Status403Forbidden, AppProblems.Forbidden, "Forbidden");
        }

        membership.ChooseStore(storeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The store was deleted between the check and the save (foreign key).
            db.ChangeTracker.Clear();
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.StoreConflict,
                "Store could not be chosen",
                "The store was removed while saving. Reload and try again.");
        }

        return Results.Ok(new ChosenStoreResponse(storeId));
    }

    /// <summary>Name 1-100 characters; the order, when given, names every section exactly once (strict names).</summary>
    private static bool TryValidate(
        StoreRequest? request, bool orderRequired, out string name, out List<StoreSection>? order, out Dictionary<string, string[]> errors)
    {
        errors = [];
        order = null;
        var normalized = Store.NormalizeName(request?.Name);
        name = normalized ?? string.Empty;
        if (request is null)
        {
            errors["body"] = ["A request body is required."];
            return false;
        }

        if (normalized is null)
        {
            errors["name"] = [$"The name must be 1 to {Store.MaxNameLength} characters, without control characters."];
        }

        if (request.SectionOrder is null)
        {
            if (orderRequired)
            {
                errors["sectionOrder"] = ["The section order is required."];
            }
        }
        else
        {
            var parsed = new List<StoreSection>();
            var allKnown = true;
            foreach (var text in request.SectionOrder)
            {
                if (StoreSections.TryParse(text, out var section))
                {
                    parsed.Add(section);
                }
                else
                {
                    allKnown = false;
                }
            }

            if (!allKnown || !StoreSectionOrder.IsComplete(parsed))
            {
                errors["sectionOrder"] = ["The section order must name every store section exactly once."];
            }
            else
            {
                order = parsed;
            }
        }

        return errors.Count == 0;
    }

    private static IResult Duplicate(string name) =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.StoreExists,
            "Store already exists",
            $"There is already a store called '{name}' in this household.");
}
