namespace Foodplanner.Api.Features.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness only: does not check the database.
        app.MapGet("/api/health", () => Results.Ok(new HealthResponse("Healthy")))
            .WithName("GetHealth")
            .Produces<HealthResponse>()
            .AllowAnonymous();
        return app;
    }

    public sealed record HealthResponse(string Status);
}
