using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Tests.Persistence;

/// <summary>Test-only tenant entity. Never part of the production model or migrations.</summary>
public sealed class TestNote : Entity, ITenantEntity
{
    private TestNote()
    {
    }

    public TestNote(string text, Guid householdId = default)
    {
        Text = text;
        HouseholdId = householdId;
    }

    public Guid HouseholdId { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public void Rename(string text) => Text = text;
}

/// <summary>
/// Test-only tenant entity on one side of a many-to-many with an implicit join entity (a shared-type
/// <c>Dictionary&lt;string, object&gt;</c>), the shape recipe and tag will have.
/// </summary>
public sealed class TestBook : Entity, ITenantEntity
{
    public Guid HouseholdId { get; private set; }

    public string Title { get; set; } = string.Empty;

    public List<TestShelf> Shelves { get; } = [];
}

/// <summary>Test-only non-tenant entity on the other side of the many-to-many.</summary>
public sealed class TestShelf : Entity
{
    public string Name { get; set; } = string.Empty;

    public List<TestBook> Books { get; } = [];
}

public sealed record FixedHousehold(Guid? HouseholdId) : ICurrentHousehold;

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The production <see cref="AppDbContext"/> plus <see cref="TestNote"/>, to exercise the tenancy conventions.</summary>
public sealed class TestDbContext : AppDbContext
{
    public TestDbContext(DbContextOptions<TestDbContext> options, ICurrentHousehold household, ITenantWriteScope writeScope, TimeProvider timeProvider)
        : base(options, household, writeScope, timeProvider)
    {
    }

    public static TestDbContext Create(string connectionString, Guid? household, ITenantWriteScope? writeScope = null, TimeProvider? timeProvider = null) =>
        new(
            new DbContextOptionsBuilder<TestDbContext>().UseSqlServer(connectionString).Options,
            new FixedHousehold(household),
            writeScope ?? new TenantWriteScope(),
            timeProvider ?? TimeProvider.System);

    protected override void ConfigureAdditionalModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestNote>(note =>
        {
            note.ToTable("TestNotes");
            note.Property(n => n.Text).HasMaxLength(200);
        });

        // Many-to-many with an implicit join entity: the conventions must skip shared-type entity types.
        modelBuilder.Entity<TestBook>().ToTable("TestBooks").HasMany(b => b.Shelves).WithMany(s => s.Books);
        modelBuilder.Entity<TestShelf>().ToTable("TestShelves");
    }
}
