using Foodplanner.Domain.Imports;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Foodplanner.Infrastructure.Persistence.Configurations;

// The import artifact (T-042). One table, no children: the draft is JSON (no foreign keys to ingredients or units; their
// ids are re-resolved through the filtered sets whenever they are written to a recipe). Household -> RecipeImport is the
// only cascade; the creating user is NO ACTION like other user references. SavedRecipeId is deliberately a plain column.

internal sealed class RecipeImportConfiguration : IEntityTypeConfiguration<RecipeImport>
{
    public void Configure(EntityTypeBuilder<RecipeImport> import)
    {
        import.ToTable("RecipeImports");
        import.Property(i => i.Source).HasConversion<string>().HasMaxLength(10).IsRequired();
        import.Property(i => i.Status).HasConversion<string>().HasMaxLength(10).IsRequired();
        import.Property(i => i.SourceUrl).HasMaxLength(RecipeImport.MaxUrlLength);
        import.Property(i => i.RawContent).HasColumnType("nvarchar(max)");
        import.Property(i => i.DraftJson).HasColumnType("nvarchar(max)");
        import.Property(i => i.RowVersion).IsRowVersion();
        import.HasIndex(i => new { i.HouseholdId, i.Status });
        import.HasIndex(i => i.CreatedByUserId);
        import.HasOne<Household>().WithMany().HasForeignKey(i => i.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        import.HasOne<AppUser>().WithMany().HasForeignKey(i => i.CreatedByUserId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
    }
}

// AI call counters (T-077): one row per household (unique), cascade with the household; one global row per month for the cap.
internal sealed class AiHouseholdUsageConfiguration : IEntityTypeConfiguration<AiHouseholdUsage>
{
    public void Configure(EntityTypeBuilder<AiHouseholdUsage> usage)
    {
        usage.ToTable("AiHouseholdUsages");
        usage.HasIndex(u => u.HouseholdId).IsUnique();
        usage.HasOne<Household>().WithMany().HasForeignKey(u => u.HouseholdId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AiGlobalUsageConfiguration : IEntityTypeConfiguration<AiGlobalUsage>
{
    public void Configure(EntityTypeBuilder<AiGlobalUsage> usage)
    {
        usage.ToTable("AiGlobalUsages");
        usage.HasIndex(u => u.MonthStart).IsUnique();
    }
}

// One row per call to Claude (T-118), for the metrics of T-121. Household -> AiCallLog is the only cascade. ImportId is a plain column.
internal sealed class AiCallLogConfiguration : IEntityTypeConfiguration<AiCallLog>
{
    public void Configure(EntityTypeBuilder<AiCallLog> log)
    {
        log.ToTable("AiCallLogs");
        log.Property(l => l.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        log.Property(l => l.Outcome).HasConversion<string>().HasMaxLength(20).IsRequired();
        log.Property(l => l.Model).HasMaxLength(AiCallLog.MaxModelLength).IsRequired();
        log.Property(l => l.Effort).HasMaxLength(AiCallLog.MaxEffortLength).IsRequired();
        log.Property(l => l.StopReason).HasMaxLength(AiCallLog.MaxStopReasonLength);
        log.HasIndex(l => l.HouseholdId);
        log.HasOne<Household>().WithMany().HasForeignKey(l => l.HouseholdId).OnDelete(DeleteBehavior.Cascade);
    }
}
