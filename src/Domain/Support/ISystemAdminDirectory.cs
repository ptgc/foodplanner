namespace Foodplanner.Domain.Support;

/// <summary>
/// Whether an account is a system admin (T-096). Evaluated per request from configuration, never cached beyond the request and
/// never read from a claim. Only the authorization handlers and the session middleware ask it (architecture test).
/// </summary>
public interface ISystemAdminDirectory
{
    Task<bool> IsAdminAsync(Guid userId, CancellationToken cancellationToken);
}
