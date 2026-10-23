using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;
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
        new TenantIsolationCase<TEntity>(create, mutate);

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
}

internal sealed class TenantIsolationCase<TEntity>(
    Func<TenantIsolationEnvironment, Guid, Task<TEntity>> create,
    Action<EntityEntry<TEntity>> mutate) : TenantIsolationCase
    where TEntity : Entity, ITenantEntity
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public override Type EntityType => typeof(TEntity);

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
        for (var type = typeof(TEntity); type is not null; type = type.BaseType)
        {
            if (type.GetProperty(nameof(ITenantEntity.HouseholdId), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly) is { } property)
            {
                property.SetValue(row, household);
                return row;
            }
        }

        throw new InvalidOperationException($"{typeof(TEntity).Name} has no HouseholdId property.");
    }

    private async Task<Guid> SeedAsync(TenantIsolationEnvironment env, Guid household)
    {
        var row = await NewRowAsync(env, household);
        await env.SaveAsync(household, row);
        return row.Id;
    }

    private static async Task<TEntity> LoadDetachedAsync(TenantIsolationEnvironment env, Guid household, Guid id)
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
            Assert.All(all, e => Assert.Equal(s.A, e.HouseholdId));

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

            // Aimed at everything: only A's rows are affected.
            Assert.Equal(1, await a.Set<TEntity>()
                .ExecuteUpdateAsync(set => set.SetProperty(e => EF.Property<DateTimeOffset>(e, "UpdatedAt"), marker), Ct));
            Assert.NotEqual(snapshotA, await env.SnapshotAsync(typeof(TEntity), s.IdA)); // not vacuous: A's row did change
            await AssertUntouchedAsync(env, s);

            Assert.Equal(1, await a.Set<TEntity>().ExecuteDeleteAsync(Ct));
            Assert.Null(await env.SnapshotAsync(typeof(TEntity), s.IdA)); // not vacuous: A's row is gone
            await AssertUntouchedAsync(env, s);
        }
    }

    private sealed record Scenario(Guid A, Guid B, Guid IdA, Guid IdB, string? BeforeB);
}
