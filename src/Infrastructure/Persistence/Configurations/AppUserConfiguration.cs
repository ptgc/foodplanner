using Foodplanner.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Foodplanner.Infrastructure.Persistence.Configurations;

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> user)
    {
        // Serves the unconfirmed-account cleanup (EmailConfirmed = 0 AND RegisteredAt < cutoff). Filtered, so it
        // only holds the few unconfirmed rows and confirming an account drops it from the index.
        user.HasIndex(u => u.RegisteredAt)
            .HasDatabaseName("IX_AspNetUsers_RegisteredAt_Unconfirmed")
            .HasFilter("[EmailConfirmed] = 0");
    }
}
