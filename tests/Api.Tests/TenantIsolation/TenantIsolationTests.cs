using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;

namespace Foodplanner.Api.Tests.TenantIsolation;

/// <summary>
/// CLAUDE.md rule 1, proven per entity: runs every registered <see cref="TenantIsolationCase"/> against a real SQL
/// Server with the production migrations. One theory row per entity, so a failure names it.
/// </summary>
public sealed class TenantIsolationTests(TenancyDatabase db) : IClassFixture<TenancyDatabase>
{
    public static TheoryData<string> Entities => [.. TenantIsolationRegistry.Names];

    private TenantIsolationEnvironment Env => new(db.ConnectionString);

    private static TenantIsolationCase Case(string name) => TenantIsolationRegistry.Find(name);

    [Theory]
    [MemberData(nameof(Entities))]
    public Task Reads_never_return_another_households_rows(string entity) => Case(entity).ReadsAreFilteredAsync(Env);

    [Theory]
    [MemberData(nameof(Entities))]
    public Task Insert_for_another_household_is_rejected(string entity) => Case(entity).InsertForOtherHouseholdIsRejectedAsync(Env);

    [Theory]
    [MemberData(nameof(Entities))]
    public Task Update_of_another_households_row_is_rejected_and_the_row_is_unchanged(string entity) => Case(entity).UpdateIsRejectedAsync(Env);

    [Theory]
    [MemberData(nameof(Entities))]
    public Task Delete_of_another_households_row_is_rejected_and_the_row_is_unchanged(string entity) => Case(entity).DeleteIsRejectedAsync(Env);

    [Theory]
    [MemberData(nameof(Entities))]
    public Task Changing_HouseholdId_is_rejected_and_the_row_is_unchanged(string entity) => Case(entity).HouseholdIdChangeIsRejectedAsync(Env);

    [Theory]
    [MemberData(nameof(Entities))]
    public Task ExecuteUpdate_and_ExecuteDelete_touch_only_the_current_household(string entity) => Case(entity).BulkOperationsRespectTheFilterAsync(Env);

    public static TheoryData<string> CatalogEntities => [.. TenantIsolationRegistry.CatalogNames];

    [Theory]
    [MemberData(nameof(CatalogEntities))]
    public Task Global_rows_are_visible_to_every_household_and_to_a_request_without_one(string entity) => Case(entity).GlobalRowsAreVisibleToEveryoneAsync(Env);

    [Theory]
    [MemberData(nameof(CatalogEntities))]
    public Task Global_rows_cannot_be_created_changed_or_claimed_by_a_household(string entity) => Case(entity).GlobalRowsAreReadOnlyAsync(Env);

    [Theory]
    [MemberData(nameof(CatalogEntities))]
    public Task Known_gap_bulk_operations_reach_global_rows(string entity) => Case(entity).BulkOperationsReachGlobalRowsAsync(Env);

    [Fact]
    public void Harness_covers_membership_and_invite_and_the_catalog()
    {
        Assert.Contains(nameof(Membership), TenantIsolationRegistry.Names);
        Assert.Contains(nameof(Invite), TenantIsolationRegistry.Names);
        Assert.Contains(nameof(Foodplanner.Domain.Catalog.Ingredient), TenantIsolationRegistry.CatalogNames);
        Assert.DoesNotContain(nameof(Membership), TenantIsolationRegistry.CatalogNames);
    }
}

/// <summary>
/// Adding a tenant entity without isolation coverage fails here, with the entity's name in the message. No database.
/// </summary>
public sealed class TenantIsolationCoverageTests
{
    private static readonly Type[] Registered = [.. TenantIsolationRegistry.Cases.Select(c => c.EntityType)];

    private static readonly Type[] CatalogRegistered = [.. TenantIsolationRegistry.Cases.Where(c => c.IsCatalog).Select(c => c.EntityType)];

    [Fact]
    public void Every_tenant_entity_in_the_production_model_is_registered_and_Household_is_exempt()
    {
        var model = TenantIsolationEnvironment.ProductionModel();

        Assert.Contains(model.GetEntityTypes(), e => e.ClrType == typeof(Membership)); // not vacuous
        Assert.Contains(model.GetEntityTypes(), e => e.ClrType == typeof(Household));
        Assert.True(TenantCoverage.Exempt.ContainsKey(typeof(Household)));
        Assert.All(TenantCoverage.Exempt, e => Assert.False(string.IsNullOrWhiteSpace(e.Value)));
        Assert.Empty(TenantCoverage.Problems(model, Registered, registeredAsCatalog: CatalogRegistered));
        Assert.Contains(model.GetEntityTypes(), e => e.ClrType == typeof(Ingredient)); // not vacuous: the catalog entity is covered
        Assert.Contains(model.GetEntityTypes(), e => e.ClrType == typeof(Unit));
        Assert.True(TenantCoverage.Exempt.ContainsKey(typeof(Unit)));
    }

    [Fact]
    public void A_catalog_entity_must_be_registered_as_a_catalog_and_a_tenant_entity_must_not_be()
    {
        var model = TenantIsolationEnvironment.ProductionModel();

        Assert.Contains(TenantCoverage.Problems(model, Registered, registeredAsCatalog: []), p => p.Contains("'Ingredient'", StringComparison.Ordinal) && p.Contains("ForCatalog", StringComparison.Ordinal));
        Assert.Contains(TenantCoverage.Problems(model, Registered, registeredAsCatalog: [.. CatalogRegistered, typeof(Membership)]), p => p.Contains("'Membership'", StringComparison.Ordinal) && p.Contains("ForCatalog", StringComparison.Ordinal));
        Assert.Contains(TenantCoverage.Problems(model, Registered.Where(t => t != typeof(Ingredient)), registeredAsCatalog: []), p => p.Contains("'Ingredient'", StringComparison.Ordinal) && p.Contains("no isolation test", StringComparison.Ordinal));
        var exemptingACatalogEntity = new Dictionary<Type, string>(TenantCoverage.Exempt) { [typeof(Ingredient)] = "sneaky" };
        Assert.Contains(TenantCoverage.Problems(model, Registered, exemptingACatalogEntity, CatalogRegistered), p => p.Contains("'Ingredient'", StringComparison.Ordinal) && p.Contains("exemption", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unregistered_tenant_entity_fails_and_the_message_names_it()
    {
        // TestDbContext adds TestNote and TestBook (tenant entities) to the production model.
        var model = TestDbContext.Create("Server=unused;Database=unused", household: null).Model;

        var problems = TenantCoverage.Problems(model, Registered);

        Assert.Contains(problems, p => p.Contains("'TestNote'", StringComparison.Ordinal) && p.Contains("no isolation test", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'TestBook'", StringComparison.Ordinal) && p.Contains("no isolation test", StringComparison.Ordinal));
        // TestShelf is not a tenant entity and not exempt: it must be classified, not silently skipped.
        Assert.Contains(problems, p => p.Contains("'TestShelf'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stale_registration_or_a_wrong_exemption_fails()
    {
        var model = TenantIsolationEnvironment.ProductionModel();

        Assert.Contains(TenantCoverage.Problems(model, [.. Registered, typeof(TestNote)]), p => p.Contains("'TestNote'", StringComparison.Ordinal) && p.Contains("stale", StringComparison.Ordinal));
        Assert.Contains(TenantCoverage.Problems(model, Registered.Where(t => t != typeof(Invite))), p => p.Contains("'Invite'", StringComparison.Ordinal));

        var withoutHousehold = TenantCoverage.Exempt.Where(e => e.Key != typeof(Household)).ToDictionary();
        Assert.Contains(TenantCoverage.Problems(model, Registered, withoutHousehold), p => p.Contains("'Household'", StringComparison.Ordinal) && p.Contains("Exempt", StringComparison.Ordinal));
        var exemptingATenantEntity = new Dictionary<Type, string>(TenantCoverage.Exempt) { [typeof(Membership)] = "sneaky" };
        Assert.Contains(TenantCoverage.Problems(model, Registered, exemptingATenantEntity), p => p.Contains("'Membership'", StringComparison.Ordinal) && p.Contains("ITenantEntity", StringComparison.Ordinal));
    }
}
