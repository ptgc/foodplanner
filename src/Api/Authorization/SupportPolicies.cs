using Foodplanner.Api.Problems;
using Foodplanner.Api.Support;
using Foodplanner.Domain.Support;
using Foodplanner.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Foodplanner.Api.Authorization;

/// <summary>Satisfied for a system admin that is not in kid mode (T-096). Not a household role: it comes from configuration, per request.</summary>
public sealed class SystemAdminRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Satisfied for an active support session for exactly the routed household (T-096); with <see cref="RequireWrite"/> also
/// only when the session is a write session.
/// </summary>
public sealed class SupportSessionRequirement(bool requireWrite) : IAuthorizationRequirement
{
    public bool RequireWrite { get; } = requireWrite;
}

/// <summary>Policy code: one of the two readers of admin status (the other is the support middleware, which asks the same directory).</summary>
internal sealed class SystemAdminHandler(ICurrentUser user, ISystemAdminDirectory directory) : AuthorizationHandler<SystemAdminRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, SystemAdminRequirement requirement)
    {
        if (user is { IsAuthenticated: true, IsKidMode: false, UserId: { } userId } && await directory.IsAdminAsync(userId, CancellationToken.None))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Policy code: the only reader of the support session state (what <see cref="SupportContextMiddleware"/> resolved). A refusal
/// carries a failure reason with the problem code, which <see cref="SupportDenials"/> turns into the 403 ProblemDetails.
/// </summary>
internal sealed class SupportSessionHandler(ISupportContext support) : AuthorizationHandler<SupportSessionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SupportSessionRequirement requirement)
    {
        if (support.Session is not { } session)
        {
            context.Fail(new AuthorizationFailureReason(
                this, support.Denial == SupportDenial.SessionMismatch ? AppProblems.SupportSessionMismatch : AppProblems.SupportSessionRequired));
        }
        else if (requirement.RequireWrite && session.Mode != SupportSessionMode.Write)
        {
            context.Fail(new AuthorizationFailureReason(this, AppProblems.SupportWriteRequired));
        }
        else
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// The 403 of a refused support request (T-096). A caller who is not an admin always gets the plain <c>forbidden</c>, so the
/// support routes do not announce themselves (and kid mode is never named). An admin gets the specific code and one
/// <c>access.denied</c> audit row (the attempted household in <c>TargetHouseholdId</c>); the body names no household.
/// </summary>
internal static class SupportDenials
{
    public static async Task HandleAsync(HttpContext context, PolicyAuthorizationResult result)
    {
        var support = context.RequestServices.GetRequiredService<ISupportContext>();
        var reason = result.AuthorizationFailure?.FailureReasons.Select(r => r.Message)
            .FirstOrDefault(m => m is AppProblems.SupportSessionRequired or AppProblems.SupportSessionMismatch or AppProblems.SupportWriteRequired);
        if (!support.IsAdmin || reason is null)
        {
            await AppProblems.WriteAsync(context, StatusCodes.Status403Forbidden, AppProblems.Forbidden, "Forbidden");
            return;
        }

        var audit = context.RequestServices.GetRequiredService<SupportAuditWriter>();
        var action = context.GetEndpoint()?.Metadata.GetMetadata<SupportAuditedMetadata>()?.Action;
        await audit.WriteDeniedAsync(reason, action, context.RequestAborted);
        await AppProblems.WriteAsync(
            context,
            StatusCodes.Status403Forbidden,
            reason,
            "Support access refused",
            reason switch
            {
                AppProblems.SupportSessionRequired => "Start a support session for the household first.",
                AppProblems.SupportSessionMismatch => "Your support session is for another household.",
                _ => "This action needs a write session.",
            });
    }
}
