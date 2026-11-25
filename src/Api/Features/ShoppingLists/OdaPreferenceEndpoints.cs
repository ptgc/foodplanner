using Foodplanner.Api.Authorization;
using Foodplanner.Api.Localization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.ShoppingLists;
using Foodplanner.Domain.ShoppingLists.Sync;
using Foodplanner.Infrastructure.Oda;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Features.ShoppingLists;

/// <summary>
/// Set the Oda product for an ingredient. <c>OdaProductId</c> is Oda's integer product id, more than 0. Foodplanner cannot check that
/// it exists at Oda (no Oda call is made here): a wrong id matches no search hit and the cart fill falls back.
/// </summary>
public sealed record OdaPreferenceRequest(long? OdaProductId);

/// <summary>One choice: the ingredient (name in the request language) and when it was set. No Oda product id or name is returned.</summary>
public sealed record OdaPreferenceResponse(Guid IngredientId, string IngredientName, DateTimeOffset SetAt);

/// <summary>
/// One item of the bulk save (T-130): the ingredient, Oda's integer product id (more than 0) and <c>Replace</c>: whether an existing choice may
/// be replaced (the client sends true only for a row the user ticked that had a choice).
/// </summary>
public sealed record OdaPreferenceBulkItem(Guid? IngredientId, long? OdaProductId, bool? Replace);

/// <summary>Up to <c>Oda:MaxBulkPreferences</c> (default 200) items, one per ingredient.</summary>
public sealed record OdaPreferenceBulkRequest(OdaPreferenceBulkItem[]? Items);

/// <summary>Counts and ids only. <c>SkippedExisting</c>: ingredients that have a choice and were not marked <c>replace</c>; they were left as they are.</summary>
public sealed record OdaPreferenceBulkResponse(int Created, int Replaced, int Unchanged, Guid[] SkippedExisting);

/// <summary>
/// The household's chosen Oda product per ingredient (T-052, Q-032): list, set and clear, Adult or Owner only, never kid mode (policies
/// <see cref="Policies.HouseholdAdult"/> and <see cref="Policies.FullSession"/>, no role is read here). Nothing here calls Oda, and
/// only the Oda product id is stored. The cart fill reads the choices (see <see cref="OdaEndpoints"/>).
/// </summary>
public static class OdaPreferenceEndpoints
{
    public static IEndpointRouteBuilder MapOdaPreferenceEndpoints(this IEndpointRouteBuilder api)
    {
        var preferences = api.MapHouseholdScopedGroup().MapGroup("/oda-preferences");
        preferences.RequireAuthorization(Policies.HouseholdAdult);
        preferences.RequireAuthorization(Policies.FullSession);

        preferences.MapGet("", ListOdaPreferences)
            .WithName("ListOdaPreferences")
            .Produces<OdaPreferenceResponse[]>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        preferences.MapPut("", SetOdaPreferences)
            .WithName("SetOdaPreferences")
            .Accepts<OdaPreferenceBulkRequest>("application/json")
            .Produces<OdaPreferenceBulkResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        preferences.MapPut("/{ingredientId:guid}", SetOdaPreference)
            .WithName("SetOdaPreference")
            .Accepts<OdaPreferenceRequest>("application/json")
            .Produces<OdaPreferenceResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        preferences.MapDelete("/{ingredientId:guid}", ClearOdaPreference)
            .WithName("ClearOdaPreference")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static async Task<IResult> ListOdaPreferences(RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.PreferredOdaProducts.AsNoTracking()
            .Select(p => new { p.IngredientId, SetAt = EF.Property<DateTimeOffset>(p, "UpdatedAt") })
            .ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.IngredientId).ToList();
        var names = await db.Ingredients.AsNoTracking().Where(i => ids.Contains(i.Id))
            .NamesByIdAsync(db, CatalogTranslation.KindIngredient, locale.Code, cancellationToken);
        var comparer = ShoppingListEndpoints.DisplayComparer(locale.Code);
        return Results.Ok(rows
            .Select(r => new OdaPreferenceResponse(r.IngredientId, names.GetValueOrDefault(r.IngredientId, string.Empty), r.SetAt))
            .OrderBy(r => r.IngredientName, comparer)
            .ThenBy(r => r.IngredientId)
            .ToArray());
    }

    private static async Task<IResult> SetOdaPreference(
        Guid ingredientId, HttpRequest http, RequestLocale locale, AppDbContext db, CancellationToken cancellationToken)
    {
        // The body is read by hand so that every bad value (missing, text, 1.5, too large, no or broken JSON) is a field error on
        // odaProductId and not a binder failure. The route documents the body as OdaPreferenceRequest (an integer) in OpenAPI.
        if (await ReadProductIdAsync(http, cancellationToken) is not { } productId)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["odaProductId"] = ["odaProductId must be a whole number above 0."],
            });
        }

        // Q-012 rule: the ingredient is resolved through the filtered set. Another household's id and an unknown id are the same 404.
        var name = await db.Ingredients.AsNoTracking().Where(i => i.Id == ingredientId)
            .NamesByIdAsync(db, CatalogTranslation.KindIngredient, locale.Code, cancellationToken);
        if (!name.TryGetValue(ingredientId, out var ingredientName))
        {
            return IngredientNotFound();
        }

        // A lost race on the unique index (or a row deleted under us) is retried once, then answered as a conflict.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var row = await db.PreferredOdaProducts.FirstOrDefaultAsync(p => p.IngredientId == ingredientId, cancellationToken);
            if (row is null)
            {
                if (await db.PreferredOdaProducts.CountAsync(cancellationToken) >= PreferredOdaProduct.MaxPerHousehold)
                {
                    return AppProblems.Problem(
                        StatusCodes.Status409Conflict,
                        AppProblems.OdaPreferenceLimit,
                        "Too many Oda choices",
                        $"A household can keep at most {PreferredOdaProduct.MaxPerHousehold} Oda product choices. Clear some first.");
                }

                row = new PreferredOdaProduct(ingredientId, productId);
                db.PreferredOdaProducts.Add(row);
            }
            else if (row.OdaProductId != productId)
            {
                row.Replace(productId);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException
                                           || ex is DbUpdateException { InnerException: Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 } })
            {
                db.ChangeTracker.Clear();
                continue;
            }

            return Results.Ok(new OdaPreferenceResponse(ingredientId, ingredientName, db.Entry(row).Property<DateTimeOffset>("UpdatedAt").CurrentValue));
        }

        return AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.OdaPreferenceConflict,
            "Choice changed at the same time",
            "Someone else changed this choice at the same moment. Nothing was lost; try again.");
    }

    /// <summary>
    /// Saves many choices in one call (T-130), all or nothing: the ticked rows of the Sync with Oda review. An item for an ingredient that
    /// has a choice is applied only with <c>replace</c> (never overwrite unless ticked). Nothing calls Oda; only ids are accepted and stored.
    /// </summary>
    private static async Task<IResult> SetOdaPreferences(
        HttpRequest http, AppDbContext db, IOptions<OdaOptions> options, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var max = Math.Max(1, options.Value.MaxBulkPreferences);
        var (items, errors) = await ReadBulkAsync(http, max, cancellationToken);
        if (items is null)
        {
            return Results.ValidationProblem(errors);
        }

        // Q-012 rule: every ingredient is resolved through the filtered set. Another household's id and an unknown id are the same 404.
        var wanted = items.Select(i => i.IngredientId).ToList();
        var known = await db.Ingredients.AsNoTracking().Where(i => wanted.Contains(i.Id)).Select(i => i.Id).ToListAsync(cancellationToken);
        if (known.Count != wanted.Count)
        {
            return IngredientNotFound();
        }

        // A lost race on the unique index (or a row deleted under us) is retried once, then answered as a conflict.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var rows = await db.PreferredOdaProducts.ToListAsync(cancellationToken);
            var plan = BulkPreferencePlanner.Plan(rows.ToDictionary(r => r.IngredientId, r => r.OdaProductId), items);
            if (plan.ExceedsLimit)
            {
                return AppProblems.Problem(
                    StatusCodes.Status409Conflict,
                    AppProblems.OdaPreferenceLimit,
                    "Too many Oda choices",
                    $"A household can keep at most {PreferredOdaProduct.MaxPerHousehold} Oda product choices. Clear some first.");
            }

            var byIngredient = rows.ToDictionary(r => r.IngredientId);
            foreach (var item in plan.Created)
            {
                db.PreferredOdaProducts.Add(new PreferredOdaProduct(item.IngredientId, item.OdaProductId));
            }

            foreach (var item in plan.Replaced)
            {
                byIngredient[item.IngredientId].Replace(item.OdaProductId);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException
                                           || ex is DbUpdateException { InnerException: Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 } })
            {
                db.ChangeTracker.Clear();
                continue;
            }

            loggers.CreateLogger("Foodplanner.Api.OdaPreferences").LogInformation(
                "Oda choices bulk save: created {Created}, replaced {Replaced}, unchanged {Unchanged}, skipped {Skipped}",
                plan.Created.Count,
                plan.Replaced.Count,
                plan.Unchanged.Count,
                plan.SkippedExisting.Count);
            return Results.Ok(new OdaPreferenceBulkResponse(plan.Created.Count, plan.Replaced.Count, plan.Unchanged.Count, [.. plan.SkippedExisting]));
        }

        return AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.OdaPreferenceConflict,
            "Choices changed at the same time",
            "Someone else changed these choices at the same moment. Nothing was lost; try again.");
    }

    /// <summary>
    /// The body is read by hand so that every bad value (missing, text, 1.5, too large, no or broken JSON) is a field error such as
    /// <c>items[2].odaProductId</c> and not a binder failure. OpenAPI documents the body as <see cref="OdaPreferenceBulkRequest"/>.
    /// </summary>
    private static async Task<(List<BulkPreferenceItem>? Items, Dictionary<string, string[]> Errors)> ReadBulkAsync(
        HttpRequest http, int max, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var limit = (max * 256) + 1024;
        try
        {
            var buffer = new byte[limit + 1];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await http.Body.ReadAsync(buffer.AsMemory(length), cancellationToken)) > 0)
            {
                length += read;
            }

            using var document = length == 0 || length > limit ? null : System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, length));
            if (document is null
                || document.RootElement is not { ValueKind: System.Text.Json.JsonValueKind.Object } root
                || !root.TryGetProperty("items", out var list) || list.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                errors["items"] = [$"items must be a list of 1 to {max} choices."];
                return (null, errors);
            }

            var count = list.GetArrayLength();
            if (count is 0 || count > max)
            {
                errors["items"] = [$"items must hold 1 to {max} choices."];
                return (null, errors);
            }

            var items = new List<BulkPreferenceItem>(count);
            var seen = new HashSet<Guid>();
            var index = 0;
            foreach (var element in list.EnumerateArray())
            {
                var prefix = $"items[{index}]";
                if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    errors[prefix] = ["Each choice must be an object."];
                }
                else
                {
                    Guid? ingredient = element.TryGetProperty("ingredientId", out var i) && i.ValueKind == System.Text.Json.JsonValueKind.String
                                       && Guid.TryParse(i.GetString(), out var parsed) && parsed != Guid.Empty ? parsed : null;
                    long? product = element.TryGetProperty("odaProductId", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number
                                    && p.TryGetInt64(out var id) && PreferredOdaProduct.IsValidProductId(id) ? id : null;
                    var replaceOk = true;
                    var replace = false;
                    if (element.TryGetProperty("replace", out var r) && r.ValueKind != System.Text.Json.JsonValueKind.Null)
                    {
                        replaceOk = r.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False;
                        replace = r.ValueKind == System.Text.Json.JsonValueKind.True;
                    }

                    if (ingredient is null)
                    {
                        errors[$"{prefix}.ingredientId"] = ["ingredientId must be an ingredient id."];
                    }
                    else if (!seen.Add(ingredient.Value))
                    {
                        errors[$"{prefix}.ingredientId"] = ["Each ingredient can be listed once."];
                    }

                    if (product is null)
                    {
                        errors[$"{prefix}.odaProductId"] = ["odaProductId must be a whole number above 0."];
                    }

                    if (!replaceOk)
                    {
                        errors[$"{prefix}.replace"] = ["replace must be true or false."];
                    }

                    if (ingredient is { } ing && product is { } prod && replaceOk)
                    {
                        items.Add(new BulkPreferenceItem(ing, prod, replace));
                    }
                }

                index++;
            }

            return errors.Count == 0 ? (items, errors) : (null, errors);
        }
        catch (System.Text.Json.JsonException)
        {
            errors["items"] = [$"items must be a list of 1 to {max} choices."];
            return (null, errors);
        }
    }

    private const int MaxBodyBytes = 1024;

    /// <summary>The valid product id of the request body, or null for anything else.</summary>
    private static async Task<long?> ReadProductIdAsync(HttpRequest http, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[MaxBodyBytes + 1];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await http.Body.ReadAsync(buffer.AsMemory(length), cancellationToken)) > 0)
            {
                length += read;
            }

            if (length is 0 or > MaxBodyBytes)
            {
                return null;
            }

            using var document = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, length));
            return document.RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root
                   && root.TryGetProperty("odaProductId", out var value)
                   && value.ValueKind == System.Text.Json.JsonValueKind.Number
                   && value.TryGetInt64(out var id)
                   && PreferredOdaProduct.IsValidProductId(id)
                ? id
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Idempotent: no row is also 204. An ingredient that is unknown or another household's is a 404.</summary>
    private static async Task<IResult> ClearOdaPreference(Guid ingredientId, AppDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Ingredients.AnyAsync(i => i.Id == ingredientId, cancellationToken))
        {
            return IngredientNotFound();
        }

        var row = await db.PreferredOdaProducts.FirstOrDefaultAsync(p => p.IngredientId == ingredientId, cancellationToken);
        if (row is not null)
        {
            db.PreferredOdaProducts.Remove(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear(); // removed by someone else in between: gone either way
            }
        }

        return Results.NoContent();
    }

    private static IResult IngredientNotFound() =>
        AppProblems.Problem(StatusCodes.Status404NotFound, AppProblems.IngredientNotFound, "Ingredient not found");
}
