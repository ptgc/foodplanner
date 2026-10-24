using Foodplanner.Api.Authorization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Features.Tags;

public sealed record CreateTagRequest(string? Name, string? Kind);

/// <summary><c>IsGlobal</c>: a shared category or tag (read-only); otherwise the household's own.</summary>
public sealed record TagResponse(Guid Id, string Name, TagKind Kind, bool IsGlobal);

/// <summary>Categories and tags (T-023): the global set plus the household's own. Any member reads; an Adult or Owner creates. No edit or delete route exists for any tag.</summary>
public static class TagEndpoints
{
    public static IEndpointRouteBuilder MapTagEndpoints(this IEndpointRouteBuilder api)
    {
        var tags = api.MapHouseholdScopedGroup().MapGroup("/tags");

        tags.MapGet("", ListTags)
            .WithName("ListTags")
            .Produces<TagResponse[]>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        tags.MapPost("", CreateTag)
            .WithName("CreateTag")
            .RequireAuthorization(Policies.HouseholdAdult)
            .Produces<TagResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    public static TagResponse ToResponse(Tag t) => new(t.Id, t.Name, t.Kind, t.HouseholdId is null);

    /// <summary>All categories first, then tags, each by name (the set is small, so it is not paged).</summary>
    private static async Task<IResult> ListTags(AppDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Tags.AsNoTracking().OrderBy(t => t.Kind).ThenBy(t => t.Name).ThenBy(t => t.Id).ToListAsync(cancellationToken);
        return Results.Ok(rows.Select(ToResponse).ToArray());
    }

    /// <summary>The name must be new among everything the household can see (its own and the global set), 409 otherwise.</summary>
    private static async Task<IResult> CreateTag(
        CreateTagRequest? request, ICurrentHousehold household, AppDbContext db, CancellationToken cancellationToken)
    {
        if (household.HouseholdId is not { } householdId)
        {
            return AppProblems.Problem(StatusCodes.Status403Forbidden, AppProblems.Forbidden, "Forbidden");
        }

        var errors = new Dictionary<string, string[]>();
        var name = Tag.NormalizeName(request?.Name);
        if (name is null)
        {
            errors["name"] = [$"The name must be 1 to {Tag.MaxNameLength} characters, without control characters."];
        }

        if (!TagKinds.TryParse(request?.Kind, out var kind))
        {
            errors["kind"] = ["The kind must be Category or Tag."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (await db.Tags.AnyAsync(t => t.Name == name, cancellationToken))
        {
            return Duplicate(name!);
        }

        var tag = new Tag(householdId, name!, kind);
        db.Tags.Add(tag);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            return Duplicate(name!);
        }

        return Results.Created((string?)null, ToResponse(tag));
    }

    private static IResult Duplicate(string name) =>
        AppProblems.Problem(
            StatusCodes.Status409Conflict,
            AppProblems.TagExists,
            "Tag already exists",
            $"There is already a category or tag called '{name}' in this household or in the shared set.");
}
