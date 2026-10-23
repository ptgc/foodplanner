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

    protected override void ConfigureAdditionalModel(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<TestNote>(note =>
        {
            note.ToTable("TestNotes");
            note.Property(n => n.Text).HasMaxLength(200);
        });
}
