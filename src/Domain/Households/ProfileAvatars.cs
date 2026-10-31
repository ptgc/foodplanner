namespace Foodplanner.Domain.Households;

/// <summary>The fixed set of avatars a kid profile can have (T-026). The client maps a key to a picture; there are no uploaded photos.</summary>
public static class ProfileAvatars
{
    public static IReadOnlyList<string> All { get; } =
        ["fox", "bear", "cat", "dog", "owl", "frog", "panda", "rabbit", "lion", "penguin", "koala", "whale"];

    /// <summary>True when <paramref name="key"/> is exactly one of <see cref="All"/> (case-sensitive).</summary>
    public static bool IsValid(string? key) => key is not null && All.Contains(key, StringComparer.Ordinal);
}
