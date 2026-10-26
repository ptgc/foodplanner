using System.Reflection;
using System.Text.Json;
using Foodplanner.Api.Features.Recipes;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Foodplanner.Api.Features.Households;

/// <summary>The household as a JSON document (T-044). <c>Entities</c> maps an entity type name to its rows.</summary>
public sealed record HouseholdExportResponse(
    string Format,
    int Version,
    DateTimeOffset ExportedAt,
    ExportedHousehold Household,
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> Entities,
    IReadOnlyList<ExportedImage> Images);

public sealed record ExportedHousehold(Guid Id, string Name);

/// <summary>A recipe picture by reference (the files are not part of the export): relative API URLs, valid for any member.</summary>
public sealed record ExportedImage(Guid RecipeId, Guid Version, string ThumbUrl, string CardUrl, string FullUrl);

/// <summary>
/// Builds the export from the EF model, so a new household-owned entity is exported without touching this code: every
/// entity type that is an <see cref="ITenantEntity"/> or an <see cref="ICatalogEntity"/> is read through the tenant
/// filter and, for catalog entities, restricted to the household's OWN rows (the filter admits the global ones too).
/// A row is a dictionary of the entity's properties, shadow columns (<c>createdAt</c>, <c>updatedAt</c>) included.
/// Properties of type <c>byte[]</c> are left out: that is the invite token hash (a secret, only its hash is stored) and
/// row versions (internal concurrency tokens).
/// </summary>
internal static class HouseholdExporter
{
    public const string FormatName = "foodplanner-household-export";
    public const int FormatVersion = 1;

    private static readonly MethodInfo ReadRowsMethod =
        typeof(HouseholdExporter).GetMethod(nameof(ReadRowsAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>The entity types an export covers: every household-owned type in the model. Public for the coverage test.</summary>
    public static IEnumerable<IEntityType> ExportedTypes(IModel model) =>
        model.GetEntityTypes()
            .Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType) || typeof(ICatalogEntity).IsAssignableFrom(t.ClrType))
            .OrderBy(t => t.ClrType.Name, StringComparer.Ordinal);

    public static async Task<HouseholdExportResponse> BuildAsync(
        AppDbContext db, Guid householdId, string householdName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var entities = new SortedDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var type in ExportedTypes(db.Model))
        {
            var task = (Task<List<IReadOnlyDictionary<string, object?>>>)ReadRowsMethod
                .MakeGenericMethod(type.ClrType)
                .Invoke(null, [db, type, householdId, cancellationToken])!;
            entities[type.ClrType.Name] = await task;
        }

        await AddMemberEmailsAsync(db, entities, cancellationToken);

        var recipes = await db.Recipes.AsNoTracking().Where(r => r.ImageVersion != null).OrderBy(r => r.Id).ToListAsync(cancellationToken);
        var images = recipes
            .Select(r => (Recipe: r, Urls: RecipeImageUrls.All(r)))
            .Where(r => r.Urls is not null)
            .Select(r => new ExportedImage(r.Recipe.Id, r.Recipe.ImageVersion!.Value, r.Urls!.ThumbUrl, r.Urls.CardUrl, r.Urls.FullUrl))
            .ToList();

        return new HouseholdExportResponse(FormatName, FormatVersion, now, new ExportedHousehold(householdId, householdName), entities, images);
    }

    private static async Task<List<IReadOnlyDictionary<string, object?>>> ReadRowsAsync<T>(
        AppDbContext db, IEntityType type, Guid householdId, CancellationToken cancellationToken)
        where T : class
    {
        // Tracked, so shadow properties (CreatedAt, UpdatedAt) can be read; cleared afterwards, this is a read.
        var rows = await db.Set<T>()
            .Where(e => EF.Property<Guid?>(e, nameof(ITenantEntity.HouseholdId)) == householdId)
            .OrderBy(e => EF.Property<Guid>(e, "Id"))
            .ToListAsync(cancellationToken);

        var properties = type.GetProperties().Where(p => p.ClrType != typeof(byte[])).ToList();
        var result = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var entry = db.Entry(row);
            var values = new Dictionary<string, object?>(properties.Count);
            foreach (var property in properties)
            {
                values[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = entry.Property(property.Name).CurrentValue;
            }

            result.Add(values);
        }

        db.ChangeTracker.Clear();
        return result;
    }

    /// <summary>Members are listed with their address, so the export says who a user id is (login identities are global, not household rows).</summary>
    private static async Task AddMemberEmailsAsync(
        AppDbContext db, SortedDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> entities, CancellationToken cancellationToken)
    {
        if (!entities.TryGetValue(nameof(Membership), out var memberships) || memberships.Count == 0)
        {
            return;
        }

        var ids = memberships.Select(m => (Guid)m["userId"]!).ToList();
        var emails = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken);
        entities[nameof(Membership)] = [.. memberships.Select(m => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(m)
        {
            ["email"] = emails.GetValueOrDefault((Guid)m["userId"]!),
        })];
    }
}
