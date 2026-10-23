using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Foodplanner.Infrastructure.Persistence.Configurations;

internal sealed class HouseholdConfiguration : IEntityTypeConfiguration<Household>
{
    public void Configure(EntityTypeBuilder<Household> household)
    {
        household.ToTable("Households");
        household.Property(h => h.Name).HasMaxLength(Household.MaxNameLength).IsRequired();
    }
}

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> membership)
    {
        membership.ToTable("Memberships");
        membership.Property(m => m.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
        membership.HasIndex(m => new { m.HouseholdId, m.UserId }).IsUnique();
        membership.HasIndex(m => m.UserId);

        // No navigations: Household is unfiltered and Membership is filtered, so the relationship is configured from
        // the dependent only (a required navigation from the root to a filtered entity raises EF warning 10622).
        membership.HasOne<Household>().WithMany().HasForeignKey(m => m.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        membership.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class InviteConfiguration : IEntityTypeConfiguration<Invite>
{
    public void Configure(EntityTypeBuilder<Invite> invite)
    {
        invite.ToTable("Invites");
        invite.Property(i => i.TokenHash).HasMaxLength(32).IsFixedLength().IsRequired();
        invite.Property(i => i.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
        invite.HasIndex(i => i.TokenHash).IsUnique();
        invite.HasIndex(i => i.HouseholdId);

        invite.HasOne<Household>().WithMany().HasForeignKey(i => i.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        // Restrict: two cascading paths from AspNetUsers to one table are not allowed in SQL Server.
        invite.HasOne<AppUser>().WithMany().HasForeignKey(i => i.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        invite.HasOne<AppUser>().WithMany().HasForeignKey(i => i.AcceptedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
