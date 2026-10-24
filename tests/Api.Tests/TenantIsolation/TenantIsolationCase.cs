using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Foodplanner.Api.Tests.TenantIsolation;

/// <summary>
/// The isolation checks for one tenant entity type. Register one per <see cref="ITenantEntity"/> in
/// <see cref="TenantIsolationRegistry"/>; the harness then proves, for that type, that household A cannot read,
/// insert, update, delete or move household B's row, by any route EF offers. Each check seeds its own two households.
/// </summary>
public abstract class TenantIsolationCase
{
    public abstract Type EntityType { get; }

    public string Name => EntityType.Name;

    public override string ToString() => Name;

    /// <summary>
    /// Registers a tenant entity. <paramref name="create"/> builds a new, unsaved row (and seeds whatever parent rows
    /// it needs, with <see cref="TenantIsolationEnvironment.SaveAsync"/>); it may ignore the household id it is given,
    /// because the harness stamps HouseholdId itself. <paramref name="mutate"/> changes one persisted column of a
    /// tracked row, so a save of it is a real UPDATE.
    /// <para>
    /// Limits: the entity must have the single Guid key <see cref="Entity.Id"/> (no composite keys), and
    /// <paramref name="create"/> must add exactly one row of this type per household (the bulk test expects exactly
    /// one affected row, so a factory that also seeds a parent of the same type would fail it).
    /// </para>
    /// </summary>
    public static TenantIsolationCase For<TEntity>(
        Func<TenantIsolationEnvironment, Guid, Task<TEntity>> create,
        Action<EntityEntry<TEntity>> mutate)
        where TEntity : Entity, ITenantEntity =>
        new TenantIsolationCase<TEntity>(create, mutate, isCatalog: false);

    /// <summary>
    /// Registers a catalog entity (<see cref="ICatalogEntity"/>: global rows with a null household, plus rows a household
    /// owns). The household-row checks are the same as for a tenant entity, with global rows allowed in the results; the
    /// harness adds the global-row checks. <paramref name="create"/> must give each new row a unique name or key
    /// (the harness makes a row global with raw SQL, to stand for a seeded row) and may use the household it is given
    /// (or ignore it: the harness stamps it).
    /// </summary>
    public static TenantIsolationCase ForCatalog<TEntity>(
        Func<TenantIsolationEnvironment, Guid, Task<TEntity>> create,
        Action<EntityEntry<TEntity>> mutate)
        where TEntity : Entity, ICatalogEntity =>
        new TenantIsolationCase<TEntity>(create, mutate, isCatalog: true);

    public abstract bool IsCatalog { get; }

    /// <summary>Catalog only. Global rows are visible to every household and to a request without a household; households' own rows stay isolated.</summary>
    public abstract Task GlobalRowsAreVisibleToEveryoneAsync(TenantIsolationEnvironment env);

    /// <summary>
    /// Catalog only. A household cannot create a row without a household, nor update, delete, re-home or claim a global
    /// row, in any of the ways a tenant row is protected; the global row is unchanged by raw SQL afterwards.
    /// </summary>
    public abstract Task GlobalRowsAreReadOnlyAsync(TenantIsolationEnvironment env);

    /// <summary>
    /// Catalog only, a pinned known gap: ExecuteUpdate/ExecuteDelete never reach the save guard, and the catalog filter
    /// admits global rows, so a bulk operation from a household DOES reach them. Architecture.Tests forbids both in
    /// production code instead (<c>TenantSourceRules.BulkWrites</c>).
    /// </summary>
    public abstract Task BulkOperationsReachGlobalRowsAsync(TenantIsolationEnvironment env);

    /// <summary>A's reads (list, Find, AsNoTracking, projection, count, Include) never see B's row; no household sees nothing.</summary>
    public abstract Task ReadsAreFilteredAsync(TenantIsolationEnvironment env);

    /// <summary>A cannot add a row for B.</summary>
    public abstract Task InsertForOtherHouseholdIsRejectedAsync(TenantIsolationEnvironment env);

    /// <summary>A cannot update B's row: a real detached copy (guard), and a forged one with A's household id (concurrency token).</summary>
    public abstract Task UpdateIsRejectedAsync(TenantIsolationEnvironment env);

    /// <summary>A cannot delete B's row, in the same two ways.</summary>
    public abstract Task DeleteIsRejectedAsync(TenantIsolationEnvironment env);

    /// <summary>Nobody can change a row's HouseholdId: not A's row to B, not B's row to A, not B's own row.</summary>
    public abstract Task HouseholdIdChangeIsRejectedAsync(TenantIsolationEnvironment env);

    /// <summary>ExecuteUpdate and ExecuteDelete from A touch only A's rows.</summary>
    public abstract Task BulkOperationsRespectTheFilterAsync(TenantIsolationEnvironment env);

    /// <summary>
    /// A new unsaved row for <paramref name="household"/> from the registered factory (parents seeded in that household),
    /// for tests that build rows of any registered type without knowing it, such as the save-time reference check (T-073).
    /// </summary>
    public abstract Task<object> NewUnsavedRowAsync(TenantIsolationEnvironment env, Guid household);

    /// <summary>Saves a new row of the type for <paramref name="household"/> and returns its id.</summary>
    public abstract Task<Guid> SeedRowAsync(TenantIsolationEnvironment env, Guid household);

    /// <summary>Catalog only: saves a row and makes it global (a stand-in for a seeded row); returns its id.</summary>
    public abstract Task<Guid> SeedGlobalRowAsync(TenantIsolationEnvironment env);
}

internal sealed class TenantIsolationCase<TEntity>(
    Func<TenantIsolationEnvironment, Guid, Task<TEntity>> create,
    Action<EntityEntry<TEntity>> mutate,
    bool isCatalog) : TenantIsolationCase
    where TEntity : Entity
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public override Type EntityType => typeof(TEntity);

    public override bool IsCatalog => isCatalog;

    private static readonly System.Reflection.PropertyInfo HouseholdIdProperty =
        typeof(TEntity).GetProperty(nameof(ITenantEntity.HouseholdId), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no HouseholdId property.");

    private static Guid? HouseholdOf(TEntity row) => (Guid?)HouseholdIdProperty.GetValue(row);

    /// <summary>
    /// Sets the household of an unsaved row through the EF change tracker's entry (the backing field), so it works for
    /// a get-only property, a private setter and a nullable one alike. <c>null</c> makes the row look global.
    /// </summary>
    private static void Stamp(TenantIsolationEnvironment env, TEntity row, Guid? household)
    {
        using var context = env.ContextFor(null);
        context.Entry(row).Property(nameof(ITenantEntity.HouseholdId)).CurrentValue = household;
    }

    /// <summary>Two fresh households, each with one row.</summary>
    private async Task<Scenario> StartAsync(TenantIsolationEnvironment env)
    {
        var a = await env.NewHouseholdAsync();
        var b = await env.NewHouseholdAsync();
        var idA = await SeedAsync(env, a);
        var idB = await SeedAsync(env, b);
        return new Scenario(a, b, idA, idB, await env.SnapshotAsync(typeof(TEntity), idB));
    }

    /// <summary>
    /// A new unsaved row that carries <paramref name="household"/> explicitly. The harness stamps the id itself, because
    /// the normal pattern (<see cref="ITenantEntity"/>) gives an entity no way to take a household in its constructor
    /// and leaves Infrastructure to assign it on Add. Factories may therefore ignore the household id.
    /// </summary>
    private async Task<TEntity> NewRowAsync(TenantIsolationEnvironment env, Guid household)
    {
        var row = await create(env, household);
        Stamp(env, row, household);
        return row;
    }

    /// <summary>A seeded stand-in: a row saved for a throwaway household, then made global with raw SQL (request code cannot do that).</summary>
    private async Task<(Guid Id, string? Snapshot)> NewGlobalRowAsync(TenantIsolationEnvironment env)
    {
        var owner = await env.NewHouseholdAsync();
        var id = await SeedAsync(env, owner);
        await env.MakeGlobalAsync(typeof(TEntity), id);
        return (id, await env.SnapshotAsync(typeof(TEntity), id));
    }

    public override async Task<object> NewUnsavedRowAsync(TenantIsolationEnvironment env, Guid household) => await NewRowAsync(env, household);

    public override Task<Guid> SeedRowAsync(TenantIsolationEnvironment env, Guid household) => SeedAsync(env, household);

    public override async Task<Guid> SeedGlobalRowAsync(TenantIsolationEnvironment env)
    {
        if (!isCatalog)
        {
            throw new InvalidOperationException($"{typeof(TEntity).Name} is not a catalog entity: it has no global rows.");
        }

        return (await NewGlobalRowAsync(env)).Id;
    }

    private async Task<Guid> SeedAsync(TenantIsolationEnvironment env, Guid household)
    {
        var row = await NewRowAsync(env, household);
        await env.SaveAsync(household, row);
        return row.Id;
    }

    private static async Task<TEntity> LoadDetachedAsync(TenantIsolationEnvironment env, Guid? household, Guid id)
    {
        await using var context = env.ContextFor(household);
        return await context.Set<TEntity>().AsNoTracking().SingleAsync(e => e.Id == id, Ct);
    }

    /// <summary>B's row is byte-for-byte what it was, and B can still see it.</summary>
    private static async Task AssertUntouchedAsync(TenantIsolationEnvironment env, Scenario s)
    {
        Assert.Equal(s.BeforeB, await env.SnapshotAsync(typeof(TEntity), s.IdB));
        await using var context = env.ContextFor(s.B);
        Assert.True(await context.Set<TEntity>().AnyAsync(e => e.Id == s.IdB, Ct), $"{typeof(TEntity).Name}: B no longer sees its own row");
    }

    /// <summary>A forged row: carries A's household id but B's row id, as an attacker-built detached entity would.</summary>
    private async Task<TEntity> ForgeAsync(TenantIsolationEnvironment env, Scenario s)
    {
        var forged = await NewRowAsync(env, s.A);
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(forged, s.IdB);
        return forged;
    }

    public override async Task ReadsAreFilteredAsync(TenantIsolationEnvironment env)
    {
        var s = await StartAsync(env);
        var name = typeof(TEntity).Name;

        await using (var a = env.ContextFor(s.A))
        {
            var all = await a.Set<TEntity>().ToListAsync(Ct);
            Assert.DoesNotContain(all, e => e.Id == s.IdB);
            Assert.Contains(all, e => e.Id == s.IdA); // not vacuous: A sees its own row
            // Only A's rows, plus (catalog entities) the global ones.
            Assert.All(all, e => Assert.True(HouseholdOf(e) == s.A || (isCatalog && HouseholdOf(e) is null), $"{name}: A saw a row of {HouseholdOf(e)}"));

            Assert.Null(await a.Set<TEntity>().FindAsync([s.IdB], Ct));
            Assert.Null(await a.Set<TEntity>().SingleOrDefaultAsync(e => e.Id == s.IdB, Ct));
            Assert.Equal(0, await a.Set<TEntity>().CountAsync(e => e.Id == s.IdB, Ct));
            Assert.False(await a.Set<TEntity>().AnyAsync(e => e.Id == s.IdB, Ct), $"{name}: AnyAsync saw B's row");
            Assert.DoesNotContain(s.IdB, await a.Set<TEntity>().Select(e => e.Id).ToListAsync(Ct));
            Assert.DoesNotContain(await a.Set<TEntity>().AsNoTracking().ToListAsync(Ct), e => e.Id == s.IdB);
            Assert.DoesNotContain(a.ChangeTracker.Entries<TEntity>(), e => e.Entity.Id == s.IdB);

            var entityType = a.Model.FindEntityType(typeof(TEntity))!;
            foreach (var navigationName in entityType.GetNavigations().Select(n => n.Name).Concat(entityType.GetSkipNavigations().Select(n => n.Name)))
            {
                var included = await a.Set<TEntity>().AsNoTracking().Include(navigationName).ToListAsync(Ct);
                Assert.DoesNotContain(included, e => e.Id == s.IdB);
            }
        }

        await using (var b = env.ContextFor(s.B))
        {
            Assert.True(await b.Set<TEntity>().AnyAsync(e => e.Id == s.IdB, Ct), $"{name}: B cannot see its own row");
            Assert.False(await b.Set<TEntity>().AnyAsync(e => e.Id == s.IdA, Ct), $"{name}: B saw A's row");
        }

        await using var none = env.ContextFor(null);
        Assert.Empty(await none.Set<TEntity>().Where(e => e.Id == s.IdA || e.Id == s.IdB).ToListAsync(Ct));
        await AssertUntouchedAsync(env, s);
    }

    public override async Task GlobalRowsAreVisibleToEveryoneAsync(TenantIsolationEnvironment env)
    {
        EnsureCatalog();
        var s = await StartAsync(env);
        var (globalId, _) = await NewGlobalRowAsync(env);

        foreach (var household in new Guid?[] { s.A, s.B, null })
        {
            await using var context = env.ContextFor(household);
            var row = await context.Set<TEntity>().SingleOrDefaultAsync(e => e.Id == globalId, Ct);
            Assert.NotNull(row);
            Assert.Null(HouseholdOf(row));
            Assert.NotNull(await context.Set<TEntity>().FindAsync([globalId], Ct));
            Assert.True(await context.Set<TEntity>().AsNoTracking().AnyAsync(e => e.Id == globalId, Ct));
            Assert.Contains(globalId, await context.Set<TEntity>().Select(e => e.Id).ToListAsync(Ct));
        }

        // And the households' own rows stay private, including from a request with no household.
        await using var a = env.ContextFor(s.A);
        Assert.False(await a.Set<TEntity>().AnyAsync(e => e.Id == s.IdB, Ct));
        await using var nobody = env.ContextFor(null);
        Assert.Empty(await nobody.Set<TEntity>().Where(e => e.Id == s.IdA || e.Id == s.IdB).ToListAsync(Ct));
    }

    public override async Task GlobalRowsAreReadOnlyAsync(TenantIsolationEnvironment env)
    {
        EnsureCatalog();
        var s = await StartAsync(env);
        var (globalId, before) = await NewGlobalRowAsync(env);
        var snapshotA = await env.SnapshotAsync(typeof(TEntity), s.IdA);

        async Task AssertGlobalUntouchedAsync()
        {
            Assert.Equal(before, await env.SnapshotAsync(typeof(TEntity), globalId));
            Assert.Equal(snapshotA, await env.SnapshotAsync(typeof(TEntity), s.IdA));
        }

        foreach (var caller in new Guid?[] { s.A, s.B, null })
        {
            // Create a row with no household, i.e. a global one.
            await using (var context = env.ContextFor(caller))
            {
                var global = await NewRowAsync(env, s.A);
                Stamp(env, global, null);
                context.Add(global);
                await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
                Assert.Null(await env.SnapshotAsync(typeof(TEntity), global.Id));
            }

            // Update: a real detached copy, through DbSet.Update, and a forged one (null household, the global row's id).
            if (caller is not null)
            {
                await using (var context = env.ContextFor(caller))
                {
                    mutate(context.Attach(await LoadDetachedAsync(env, null, globalId)));
                    await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
                }

                await using (var context = env.ContextFor(caller))
                {
                    var row = await LoadDetachedAsync(env, null, globalId);
                    mutate(context.Attach(row));
                    context.Update(row);
                    await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
                }

                await using (var context = env.ContextFor(caller))
                {
                    var forged = await NewRowAsync(env, s.A);
                    Stamp(env, forged, null);
                    typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(forged, globalId);
                    var entry = context.Entry(forged);
                    entry.State = EntityState.Unchanged;
                    mutate(entry);
                    entry.State = EntityState.Modified;
                    await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
                }

                // Delete: tracked from a query (the household sees the row), and forged.
                await using (var context = env.ContextFor(caller))
                {
                    context.Remove(await context.Set<TEntity>().SingleAsync(e => e.Id == globalId, Ct));
                    await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
                }

                await using (var context = env.ContextFor(caller))
                {
                    var forged = await NewRowAsync(env, s.A);
                    Stamp(env, forged, null);
                    typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(forged, globalId);
                    context.Entry(forged).State = EntityState.Unchanged;
                    context.Remove(forged);
                    await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
                }

                // Claim the global row for the household, and release the household's own row into the global pool.
                await using (var context = env.ContextFor(caller))
                {
                    var row = await context.Set<TEntity>().SingleAsync(e => e.Id == globalId, Ct);
                    context.Entry(row).Property(nameof(ITenantEntity.HouseholdId)).CurrentValue = caller;
                    await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
                }

                await using (var context = env.ContextFor(caller))
                {
                    var own = await context.Set<TEntity>().SingleAsync(e => e.Id == (caller == s.A ? s.IdA : s.IdB), Ct);
                    context.Entry(own).Property(nameof(ITenantEntity.HouseholdId)).CurrentValue = null;
                    await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
                }
            }

            await AssertGlobalUntouchedAsync();
            Assert.NotNull(await env.SnapshotAsync(typeof(TEntity), s.IdB));
        }
    }

    public override async Task BulkOperationsReachGlobalRowsAsync(TenantIsolationEnvironment env)
    {
        EnsureCatalog();
        var (globalId, before) = await NewGlobalRowAsync(env);
        var household = await env.NewHouseholdAsync();
        var marker = DateTimeOffset.UtcNow.AddYears(1);

        await using var context = env.ContextFor(household);
        var updated = await context.Set<TEntity>().Where(e => e.Id == globalId)
            .ExecuteUpdateAsync(set => set.SetProperty(e => EF.Property<DateTimeOffset>(e, "UpdatedAt"), marker), Ct);

        Assert.Equal(1, updated); // the hole: a household reached a global row
        Assert.NotEqual(before, await env.SnapshotAsync(typeof(TEntity), globalId));
        Assert.Equal(1, await context.Set<TEntity>().Where(e => e.Id == globalId).ExecuteDeleteAsync(Ct));
        Assert.Null(await env.SnapshotAsync(typeof(TEntity), globalId));
    }

    private IQueryable<TEntity> Mine(AppDbContext context, Guid household) =>
        isCatalog
            ? context.Set<TEntity>().Where(e => EF.Property<Guid?>(e, nameof(ITenantEntity.HouseholdId)) == household)
            : context.Set<TEntity>();

    private void EnsureCatalog()
    {
        if (!isCatalog)
        {
            throw new InvalidOperationException($"{typeof(TEntity).Name} is not registered as a catalog entity.");
        }
    }

    public override async Task InsertForOtherHouseholdIsRejectedAsync(TenantIsolationEnvironment env)
    {
        var s = await StartAsync(env);

        await using (var a = env.ContextFor(s.A))
        {
            var intruder = await NewRowAsync(env, s.B);
            a.Add(intruder);
            await Assert.ThrowsAsync<TenancyViolationException>(() => a.SaveChangesAsync(Ct));
            Assert.Null(await env.SnapshotAsync(typeof(TEntity), intruder.Id));
        }

        await AssertUntouchedAsync(env, s);
    }

    public override async Task UpdateIsRejectedAsync(TenantIsolationEnvironment env)
    {
        var s = await StartAsync(env);

        // A real detached copy of B's row, attached and changed.
        await using (var a = env.ContextFor(s.A))
        {
            mutate(a.Attach(await LoadDetachedAsync(env, s.B, s.IdB)));
            await Assert.ThrowsAsync<TenancyViolationException>(() => a.SaveChangesAsync(Ct));
        }

        await AssertUntouchedAsync(env, s);

        // The same through DbSet.Update, which marks every column modified.
        await using (var a = env.ContextFor(s.A))
        {
            var row = await LoadDetachedAsync(env, s.B, s.IdB);
            var entry = a.Attach(row);
            mutate(entry);
            a.Update(row);
            await Assert.ThrowsAsync<TenancyViolationException>(() => a.SaveChangesAsync(Ct));
        }

        await AssertUntouchedAsync(env, s);

        // Forged: A's household id on B's row id. The guard sees an update of A's own row, but the HouseholdId
        // concurrency token makes the UPDATE match nothing.
        await using (var a = env.ContextFor(s.A))
        {
            var forged = await ForgeAsync(env, s);
            var entry = a.Entry(forged);
            entry.State = EntityState.Unchanged;
            mutate(entry);
            entry.State = EntityState.Modified;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => a.SaveChangesAsync(Ct));
        }

        await AssertUntouchedAsync(env, s);
    }

    public override async Task DeleteIsRejectedAsync(TenantIsolationEnvironment env)
    {
        var s = await StartAsync(env);

        await using (var a = env.ContextFor(s.A))
        {
            a.Remove(await LoadDetachedAsync(env, s.B, s.IdB));
            await Assert.ThrowsAsync<TenancyViolationException>(() => a.SaveChangesAsync(Ct));
        }

        await AssertUntouchedAsync(env, s);

        await using (var a = env.ContextFor(s.A))
        {
            var forged = await ForgeAsync(env, s);
            a.Entry(forged).State = EntityState.Unchanged;
            a.Remove(forged);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => a.SaveChangesAsync(Ct));
        }

        await AssertUntouchedAsync(env, s);

        // Not vacuous: the same delete from the owner works.
        await using (var b = env.ContextFor(s.B))
        {
            b.Remove(await b.Set<TEntity>().SingleAsync(e => e.Id == s.IdB, Ct));
            await b.SaveChangesAsync(Ct);
        }

        Assert.Null(await env.SnapshotAsync(typeof(TEntity), s.IdB));
    }

    public override async Task HouseholdIdChangeIsRejectedAsync(TenantIsolationEnvironment env)
    {
        var s = await StartAsync(env);
        var snapshotA = await env.SnapshotAsync(typeof(TEntity), s.IdA);

        // A moves its own row to B.
        await using (var a = env.ContextFor(s.A))
        {
            var own = await a.Set<TEntity>().SingleAsync(e => e.Id == s.IdA, Ct);
            a.Entry(own).Property(nameof(ITenantEntity.HouseholdId)).CurrentValue = s.B;
            await Assert.ThrowsAsync<TenancyViolationException>(() => a.SaveChangesAsync(Ct));
        }

        Assert.Equal(snapshotA, await env.SnapshotAsync(typeof(TEntity), s.IdA));

        // A takes B's row over by attaching a detached copy and re-homing it.
        await using (var a = env.ContextFor(s.A))
        {
            var theirs = a.Attach(await LoadDetachedAsync(env, s.B, s.IdB));
            theirs.Property(nameof(ITenantEntity.HouseholdId)).CurrentValue = s.A;
            await Assert.ThrowsAsync<TenancyViolationException>(() => a.SaveChangesAsync(Ct));
        }

        await AssertUntouchedAsync(env, s);

        // Even the owner cannot move its row.
        await using (var b = env.ContextFor(s.B))
        {
            var own = await b.Set<TEntity>().SingleAsync(e => e.Id == s.IdB, Ct);
            b.Entry(own).Property(nameof(ITenantEntity.HouseholdId)).CurrentValue = s.A;
            await Assert.ThrowsAsync<TenancyViolationException>(() => b.SaveChangesAsync(Ct));
        }

        await AssertUntouchedAsync(env, s);
    }

    public override async Task BulkOperationsRespectTheFilterAsync(TenantIsolationEnvironment env)
    {
        var s = await StartAsync(env);
        var snapshotA = await env.SnapshotAsync(typeof(TEntity), s.IdA);
        var marker = DateTimeOffset.UtcNow.AddYears(1);

        await using (var a = env.ContextFor(s.A))
        {
            // Aimed at B's row by id.
            Assert.Equal(0, await a.Set<TEntity>().Where(e => e.Id == s.IdB)
                .ExecuteUpdateAsync(set => set.SetProperty(e => EF.Property<DateTimeOffset>(e, "UpdatedAt"), marker), Ct));
            Assert.Equal(0, await a.Set<TEntity>().Where(e => e.Id == s.IdB).ExecuteDeleteAsync(Ct));
            await AssertUntouchedAsync(env, s);

            // Aimed at everything of A's: only A's rows are affected. (A catalog set also admits global rows, which a bulk operation would reach: pinned separately, so aim at A's own rows.)
            Assert.Equal(1, await Mine(a, s.A)
                .ExecuteUpdateAsync(set => set.SetProperty(e => EF.Property<DateTimeOffset>(e, "UpdatedAt"), marker), Ct));
            Assert.NotEqual(snapshotA, await env.SnapshotAsync(typeof(TEntity), s.IdA)); // not vacuous: A's row did change
            await AssertUntouchedAsync(env, s);

            Assert.Equal(1, await Mine(a, s.A).ExecuteDeleteAsync(Ct));
            Assert.Null(await env.SnapshotAsync(typeof(TEntity), s.IdA)); // not vacuous: A's row is gone
            await AssertUntouchedAsync(env, s);
        }
    }

    private sealed record Scenario(Guid A, Guid B, Guid IdA, Guid IdB, string? BeforeB);
}
