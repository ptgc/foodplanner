using Foodplanner.Domain.Support;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Support;

/// <summary>
/// The configured allow-list as an <see cref="ISystemAdminDirectory"/> (T-096). The list is read from configuration on every call
/// (so a change of the setting is seen by the next request), the account is looked up by id on every call (its CURRENT confirmed
/// email and lockout), and the answer is remembered for this request only (the service is scoped). Never a claim, never a cookie.
/// A list longer than the cap is refused at startup; if one ever appears at run time nobody is an admin until it is fixed.
/// </summary>
internal sealed class ConfigSystemAdminDirectory(IConfiguration configuration, AppDbContext db, TimeProvider time) : ISystemAdminDirectory
{
    private readonly Dictionary<Guid, bool> _answers = [];

    public async Task<bool> IsAdminAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_answers.TryGetValue(userId, out var known))
        {
            return known;
        }

        var list = SystemAdminList.Normalize(configuration.GetSection($"{SupportOptions.Section}:{nameof(SupportOptions.SystemAdminEmails)}").Get<string[]>());
        if (list.Length is 0 or > SystemAdminList.MaxEntries)
        {
            return _answers[userId] = false;
        }

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Email, u.EmailConfirmed, u.LockoutEnd })
            .FirstOrDefaultAsync(cancellationToken);
        var lockedOut = user?.LockoutEnd is { } until && until > time.GetUtcNow();
        return _answers[userId] = user is not null && SystemAdminList.IsAdmin(list, user.Email, user.EmailConfirmed, lockedOut);
    }
}

/// <summary>
/// "Is the signed-in caller a system admin" for the <c>/api/auth/me</c> answer (T-096). Kid mode is never an admin. This is the
/// one feature-code route to the admin status (the architecture test names its only caller); policies decide access, this only
/// tells the web app whether to show the support entry.
/// </summary>
public interface ISystemAdminStatus
{
    Task<bool> IsSystemAdminAsync(CancellationToken cancellationToken);
}

internal sealed class SystemAdminStatus(ICurrentUser user, ISystemAdminDirectory directory) : ISystemAdminStatus
{
    public async Task<bool> IsSystemAdminAsync(CancellationToken cancellationToken) =>
        user is { IsAuthenticated: true, IsKidMode: false, UserId: { } userId } && await directory.IsAdminAsync(userId, cancellationToken);
}
