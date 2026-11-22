using Foodplanner.Api.Authorization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Support;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Api.Features.Support;

/// <summary>
/// What a household sees of support (T-096): <c>GET /api/households/{householdId}/support-access</c> for Owner and Adult. It is an
/// ordinary household route, found through the caller's membership; the rows are those of the policy-verified household only.
/// The admin appears nowhere (no name, no address), and neither the note nor the detail is returned.
/// </summary>
public static class SupportAccessEndpoints
{
    public static IEndpointRouteBuilder MapSupportAccessEndpoints(this IEndpointRouteBuilder api)
    {
        var household = api.MapHouseholdScopedGroup();

        household.MapGet("/support-access", List)
            .RequireAuthorization(Policies.HouseholdAdult)
            .WithName("ListSupportAccess")
            .Produces<SupportAccessPage>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static async Task<IResult> List(
        int? page, int? pageSize, ICurrentHousehold current, SupportAccessLog log, CancellationToken cancellationToken)
    {
        if (current.HouseholdId is not { } householdId)
        {
            return AppProblems.Problem(StatusCodes.Status403Forbidden, AppProblems.Forbidden, "Forbidden");
        }

        var errors = new Dictionary<string, string[]>();
        var (pageNumber, size) = SupportEndpoints.Paging(page, pageSize, errors, SupportAccessLog.DefaultPageSize);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        return Results.Ok(await log.ForHouseholdAsync(householdId, pageNumber, Math.Min(size, SupportAccessLog.MaxPageSize), cancellationToken));
    }
}
