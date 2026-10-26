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
        import.HasOne<AppUser>().WithMany().HasForeignKey(i => i.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
