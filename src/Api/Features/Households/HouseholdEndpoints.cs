using Foodplanner.Api.Authorization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Security;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Features.Households;

public sealed record CreateHouseholdRequest(string? Name);

public sealed record HouseholdResponse(Guid Id, string Name, HouseholdRole Role);

public sealed record CreateInviteRequest(string? Role);

public sealed record InviteResponse(string Token, DateTimeOffset ExpiresAt, HouseholdRole Role);

public sealed record AcceptInviteResponse(Guid HouseholdId, HouseholdRole Role);

/// <summary>Section <c>Households</c>.</summary>
public sealed class HouseholdOptions
{
    public const string Section = "Households";

    [System.ComponentModel.DataAnnotations.Range(1, 90)]
    public int InviteLifetimeDays { get; set; } = 7;
}

public static class HouseholdEndpoints
{
    // The same title and detail for unknown, expired and used tokens, so the response says nothing about the invite.
    private const string InviteInvalidTitle = "Invite is not valid";
    private const string InviteInvalidDetail = "The invite link is unknown, expired or already used.";

    public static IEndpointRouteBuilder MapHouseholdEndpoints(this IEndpointRouteBuilder api)
    {
        var households = api.MapGroup("/households");

        households.MapPost("", CreateHousehold)
            .WithName("CreateHousehold")
            .Produces<HouseholdResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        households.MapGet("", ListHouseholds)
            .WithName("ListHouseholds")
            .Produces<HouseholdResponse[]>();

        // Household-scoped: /api/households/{householdId}/... (Q-004). Only the Owner may invite.
        var invites = api.MapHouseholdScopedGroup().MapGroup("/invites").RequireAuthorization(Policies.HouseholdOwner);
        invites.MapPost("", CreateInvite)
            .WithName("CreateInvite")
            .Produces<InviteResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapPost("/invites/{token}/accept", AcceptInvite)
            .WithName("AcceptInvite")
            .Produces<AcceptInviteResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .RequireRateLimiting(RateLimitPolicies.InviteAccept);

        return api;
    }

    private static async Task<IResult> CreateHousehold(
        CreateHouseholdRequest? request, ICurrentUser user, ITenantResolution resolution, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return Unauthenticated();
        }

        var name = Household.NormalizeName(request?.Name);
        if (name is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"The name must be 1 to {Household.MaxNameLength} characters."],
            });
        }

        var created = await resolution.CreateHouseholdWithOwner(name, userId, cancellationToken);
        return Results.Created($"/api/households/{created.HouseholdId}", new HouseholdResponse(created.HouseholdId, created.Name, created.Role));
    }

    private static async Task<IResult> ListHouseholds(ICurrentUser user, ITenantResolution resolution, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return Unauthenticated();
        }

        var memberships = await resolution.GetMembershipsForUser(userId, cancellationToken);
        return Results.Ok(memberships.Select(m => new HouseholdResponse(m.HouseholdId, m.Name, m.Role)).ToArray());
    }

    private static async Task<IResult> CreateInvite(
        CreateInviteRequest? request,
        ICurrentUser user,
        ICurrentHousehold household,
        AppDbContext db,
        TimeProvider time,
        IOptions<HouseholdOptions> options,
        CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return Unauthenticated();
        }

        // The policy guarantees a household context; a missing one is a wiring bug, so refuse rather than guess.
        if (household.HouseholdId is not { } householdId)
        {
            return AppProblems.Problem(StatusCodes.Status403Forbidden, AppProblems.Forbidden, "Forbidden");
        }

        if (!HouseholdRoles.TryParse(request?.Role, out var role) || !role.IsInvitable())
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["role"] = ["The role must be Adult or Child."],
            });
        }

        var token = InviteToken.Generate();
        var expiresAt = time.GetUtcNow().AddDays(options.Value.InviteLifetimeDays);
        db.Invites.Add(new Invite(householdId, InviteToken.Hash(token), role, expiresAt, userId));
        await db.SaveChangesAsync(cancellationToken);

        // The plain token is returned here and nowhere else.
        return Results.Created((string?)null, new InviteResponse(token, expiresAt, role));
    }

    private static async Task<IResult> AcceptInvite(
        string token, ICurrentUser user, IInviteRedemption redemption, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return Unauthenticated();
        }

        var result = await redemption.AcceptAsync(token, userId, cancellationToken);
        return result.Outcome switch
        {
            InviteRedemptionOutcome.Joined => Results.Ok(new AcceptInviteResponse(result.HouseholdId!.Value, result.Role!.Value)),
            InviteRedemptionOutcome.AlreadyMember => AppProblems.Problem(
                StatusCodes.Status409Conflict, AppProblems.AlreadyMember, "Already a member", "You already belong to this household."),
            _ => AppProblems.Problem(StatusCodes.Status410Gone, AppProblems.InviteInvalid, InviteInvalidTitle, InviteInvalidDetail),
        };
    }

    private static IResult Unauthenticated() =>
        AppProblems.Problem(StatusCodes.Status401Unauthorized, AppProblems.Unauthenticated, "Not signed in");
}
