using Foodplanner.Domain.Households;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Foodplanner.Infrastructure.Persistence.Configurations;

// Kid profiles (T-026). No navigations. Household -> MemberProfile and Household -> RecipeLike are the only cascades; the
// likes' foreign keys to profiles and recipes are NO ACTION (SQL Server rejects a second cascade path, and the endpoints
// delete the likes first in the same transaction).

internal sealed class MemberProfileConfiguration : IEntityTypeConfiguration<MemberProfile>
{
    public void Configure(EntityTypeBuilder<MemberProfile> profile)
    {
        profile.ToTable("MemberProfiles");
        profile.Property(p => p.DisplayName).HasMaxLength(MemberProfile.MaxNameLength).IsRequired();
        profile.Property(p => p.Avatar).HasMaxLength(20).IsRequired();
        // Unique per household; SQL Server's default (case-insensitive) collation makes "Mia" and "mia" collide.
        profile.HasIndex(p => new { p.HouseholdId, p.DisplayName }).IsUnique();
        profile.HasOne<Household>().WithMany().HasForeignKey(p => p.HouseholdId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RecipeLikeConfiguration : IEntityTypeConfiguration<RecipeLike>
{
    public void Configure(EntityTypeBuilder<RecipeLike> like)
    {
        like.ToTable("RecipeLikes");
        like.HasIndex(l => new { l.MemberProfileId, l.RecipeId }).IsUnique();
        like.HasIndex(l => l.RecipeId);
        like.HasOne<Household>().WithMany().HasForeignKey(l => l.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        like.HasOne<MemberProfile>().WithMany().HasForeignKey(l => l.MemberProfileId).OnDelete(DeleteBehavior.Restrict);
        like.HasOne<Recipe>().WithMany().HasForeignKey(l => l.RecipeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UserPinConfiguration : IEntityTypeConfiguration<UserPin>
{
    public void Configure(EntityTypeBuilder<UserPin> pin)
    {
        pin.ToTable("UserPins");
        pin.HasKey(p => p.UserId);
        pin.Property(p => p.Hash).HasMaxLength(256).IsRequired();
        pin.Property(p => p.RowVersion).IsRowVersion();
        pin.HasOne<AppUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
