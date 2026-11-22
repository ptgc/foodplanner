using Foodplanner.Domain.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Foodplanner.Infrastructure.Persistence.Configurations;

// System admin support (T-096). Both tables are GLOBAL records about support, not household data, and have NO foreign keys: not to
// Households and not to AspNetUsers, so deleting a household or an account never deletes or alters a support record. The household
// is a plain column named TargetHouseholdId (the name HouseholdId is reserved for tenant and catalog entities by the model rules).

internal sealed class SupportSessionConfiguration : IEntityTypeConfiguration<SupportSession>
{
    public void Configure(EntityTypeBuilder<SupportSession> session)
    {
        session.ToTable("SupportSessions");
        session.HasKey(s => s.Id);
        session.Property(s => s.Id).ValueGeneratedNever();
        session.Property(s => s.Mode).HasConversion<string>().HasMaxLength(10).IsRequired();
        session.Property(s => s.ReasonCategory).HasConversion<string>().HasMaxLength(20).IsRequired();
        session.Property(s => s.ReasonNote).HasMaxLength(SupportSession.MaxNoteLength).IsRequired();
        session.Property(s => s.EndedReason).HasMaxLength(20);

        // At most one active session per admin, even for two parallel starts: the second insert violates this index.
        session.HasIndex(s => s.AdminUserId).IsUnique().HasFilter("[EndedAt] IS NULL").HasDatabaseName("UX_SupportSessions_AdminUserId_Active");
        session.HasIndex(s => s.TargetHouseholdId);
    }
}

internal sealed class SupportAuditEntryConfiguration : IEntityTypeConfiguration<SupportAuditEntry>
{
    public void Configure(EntityTypeBuilder<SupportAuditEntry> entry)
    {
        entry.ToTable("SupportAuditEntries");
        entry.HasKey(e => e.Id);
        entry.Property(e => e.Id).ValueGeneratedNever();
        entry.Property(e => e.AdminEmail).HasMaxLength(256).IsRequired();
        entry.Property(e => e.TargetHouseholdName).HasMaxLength(100);
        entry.Property(e => e.Action).HasMaxLength(40).IsRequired();
        entry.Property(e => e.Mode).HasConversion<string>().HasMaxLength(10);
        entry.Property(e => e.ReasonCategory).HasConversion<string>().HasMaxLength(20);
        entry.Property(e => e.ReasonNote).HasMaxLength(SupportSession.MaxNoteLength);
        entry.Property(e => e.Detail).HasMaxLength(SupportAuditEntry.MaxDetailLength);
        entry.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(10).IsRequired();
        entry.HasIndex(e => new { e.TargetHouseholdId, e.At });
        entry.HasIndex(e => e.At);
    }
}
