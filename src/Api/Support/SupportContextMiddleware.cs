using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Support;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Support;

/// <summary>
/// Runs after authentication and routing and before authorization, like <see cref="HouseholdContextMiddleware"/> and for the same
/// reason (policies run before endpoint filters). For the endpoints under <c>/api/support/households/{targetHouseholdId}</c> it
/// (1) requires a system admin that is not in kid mode, (2) loads the admin's active, unexpired session, (3) requires the route
/// household to equal the SESSION's household, and (4) only then sets the household context from the SESSION row, with
/// <c>Role = null</c>. Any failed step leaves the household context empty: the tenant filter then returns no rows. It adds no
/// filter bypass and never gives a membership. On the other support routes (search, the sessions) it only notes the admin's active
/// session, for the audit row.
/// </summary>
internal sealed class SupportContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUser user,
        ISystemAdminDirectory directory,
        RequestSupportContext support,
        RequestCurrentHousehold household,
        AppDbContext db,
        TimeProvider time)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<SupportRouteMetadata>() is not null
            && user is { IsAuthenticated: true, IsKidMode: false, UserId: { } userId }
            && await directory.IsAdminAsync(userId, context.RequestAborted))
        {
            support.SetAdmin();
            var session = await FindActiveSessionAsync(db, userId, time.GetUtcNow(), context.RequestAborted);
            if (endpoint.Metadata.GetMetadata<SupportScopedMetadata>() is not null)
            {
                Resolve(context, session, support, household);
            }
            else if (session is not null)
            {
                support.NoteActive(session);
            }
        }

        await next(context);
    }

    private static Task<SupportSessionInfo?> FindActiveSessionAsync(AppDbContext db, Guid adminUserId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.SupportSessions.AsNoTracking()
            .Where(s => s.AdminUserId == adminUserId && s.EndedAt == null && s.ExpiresAt > now)
            .Select(s => new SupportSessionInfo(
                s.Id, s.AdminUserId, s.TargetHouseholdId, s.Mode, s.ReasonCategory, s.ReasonNote, s.StartedAt, s.ExpiresAt))
            .FirstOrDefaultAsync(cancellationToken);

    private static void Resolve(HttpContext context, SupportSessionInfo? session, RequestSupportContext support, RequestCurrentHousehold household)
    {
        var routeValue = context.GetRouteValue(SupportRoutes.RouteParameter)?.ToString();
        Guid? routeHousehold = Guid.TryParse(routeValue, out var parsed) ? parsed : null;
        if (session is null)
        {
            support.Refused(SupportDenial.SessionRequired, null, routeHousehold);
        }
        else if (routeHousehold != session.HouseholdId)
        {
            support.Refused(SupportDenial.SessionMismatch, session, routeHousehold);
        }
        else
        {
            // The session row, not the route value, sets the household the filter uses.
            support.Matched(session, session.HouseholdId);
            household.SetSupport(session.HouseholdId, session.Id);
        }
    }
}
