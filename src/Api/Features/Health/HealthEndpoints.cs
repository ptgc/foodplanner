using Foodplanner.Infrastructure.Persistence;

namespace Foodplanner.Api.Features.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness only: does not check the database.
        app.MapGet("/health", () => Results.Ok(new HealthResponse("Healthy")))
            .WithName("GetHealth")
            .Produces<HealthResponse>()
            .AllowAnonymous();

        // Readiness: reports whether SQL Server answers. Details never include the connection string.
        app.MapGet("/health/ready", async (IDatabaseReadiness readiness, CancellationToken ct) =>
            {
                var status = await readiness.CheckAsync(ct);
                return status switch
                {
                    DatabaseStatus.Up => Results.Ok(new ReadyResponse("Healthy", "Up")),
                    DatabaseStatus.NotConfigured => Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Database not configured",
                        detail: "ConnectionStrings:Default is not set. Set it with dotnet user-secrets (Development) or the ConnectionStrings__Default environment variable."),
                    _ => Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Database unavailable",
                        detail: "SQL Server could not be reached."),
                };
            })
            .WithName("GetHealthReady")
            .Produces<ReadyResponse>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .AllowAnonymous();
        return app;
    }

    public sealed record HealthResponse(string Status);

    public sealed record ReadyResponse(string Status, string Database);
}
