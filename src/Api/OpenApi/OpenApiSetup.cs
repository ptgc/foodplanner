using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

namespace Foodplanner.Api.OpenApi;

public static class OpenApiSetup
{
    private const string ProblemDetailsName = "ProblemDetails";

    public static IServiceCollection AddFoodplannerOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer(async (document, context, cancellationToken) =>
            {
                document.Info.Title = "Foodplanner API";

                // The web app is served same-origin (/api proxy), so the document must not pin a host.
                document.Servers = [];

                var problem = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), cancellationToken: cancellationToken);
                document.Components ??= new OpenApiComponents();
                document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
                document.Components.Schemas[ProblemDetailsName] = problem;
            });

            // Every operation can fail with RFC 9457 ProblemDetails (CLAUDE.md rule 4).
            options.AddOperationTransformer((operation, _, _) =>
            {
                operation.Responses ??= [];
                operation.Responses["default"] = new OpenApiResponse
                {
                    Description = "Error (RFC 9457 ProblemDetails)",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference(ProblemDetailsName) },
                    },
                };
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
