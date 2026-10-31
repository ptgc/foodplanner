using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Households;

/// <summary>
/// A kid profile (T-026): a named, credential-less person in a household that a signed-in account can act as. It is not a
/// user and not a membership: no email, no password, no link to an account. It has a name, an avatar and likes
/// (<see cref="RecipeLike"/>). The name is unique per household, ignoring case. HouseholdId is assigned by Infrastructure.
/// </summary>
public sealed class MemberProfile : Entity, ITenantEntity
{
    public const int MaxNameLength = 30;
    public const int MaxPerHousehold = 10;

    private MemberProfile()
    {
    }

    public MemberProfile(string displayName, string avatar)
    {
        DisplayName = NormalizeName(displayName) ?? throw new ArgumentException("Invalid profile name.", nameof(displayName));
        Avatar = ProfileAvatars.IsValid(avatar) ? avatar : throw new ArgumentException("Unknown avatar.", nameof(avatar));
    }

    public Guid HouseholdId { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public string Avatar { get; private set; } = string.Empty;

    public void Change(string displayName, string avatar)
    {
        DisplayName = NormalizeName(displayName) ?? throw new ArgumentException("Invalid profile name.", nameof(displayName));
        Avatar = ProfileAvatars.IsValid(avatar) ? avatar : throw new ArgumentException("Unknown avatar.", nameof(avatar));
    }

    /// <summary>Trims and collapses inner whitespace; null when empty, too long or containing control characters.</summary>
    public static string? NormalizeName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var collapsed = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length is 0 or > MaxNameLength || collapsed.Any(char.IsControl) ? null : collapsed;
    }
}
