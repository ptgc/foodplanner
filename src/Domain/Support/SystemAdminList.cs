namespace Foodplanner.Domain.Support;

/// <summary>
/// Who is a system admin (T-096): the confirmed account whose email is on the configured allow-list. Pure rules, so they are
/// testable without a host. The list lives in configuration only; nothing in the app can write it.
/// </summary>
public static class SystemAdminList
{
    public const int MaxEntries = 5;

    /// <summary>Trimmed, empty entries dropped, duplicates (ignoring case) removed.</summary>
    public static string[] Normalize(IEnumerable<string?>? configured) =>
        (configured ?? [])
            .Select(e => e?.Trim())
            .Where(e => !string.IsNullOrEmpty(e))
            .Select(e => e!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool IsTooLong(IEnumerable<string?>? configured) => Normalize(configured).Length > MaxEntries;

    /// <summary>
    /// True only when the address is on the list (ignoring case) AND the account's email is confirmed AND the account is not
    /// locked out. An unconfirmed registration of an allow-listed address is never an admin.
    /// </summary>
    public static bool IsAdmin(IEnumerable<string?>? configured, string? email, bool emailConfirmed, bool lockedOut)
    {
        if (string.IsNullOrWhiteSpace(email) || !emailConfirmed || lockedOut)
        {
            return false;
        }

        var address = email.Trim();
        return Normalize(configured).Any(entry => string.Equals(entry, address, StringComparison.OrdinalIgnoreCase));
    }
}
