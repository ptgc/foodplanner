using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.OpenApi;

namespace Foodplanner.Api.OpenApi;

public static partial class OpenApiSetup
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

            // Route parameters that come from a MapGroup prefix (e.g. {householdId}) are not bound by the handler, so
            // the generator omits them. Declare every template parameter that is missing (name, in: path, required,
            // uuid for :guid constraints) so the generated TS client types them.
            options.AddOperationTransformer((operation, context, _) =>
            {
                var relative = context.Description.RelativePath;
                if (relative is null)
                {
                    return Task.CompletedTask;
                }

                var pattern = FindPattern(context, relative);
                foreach (var name in TemplateParameters(relative))
                {
                    operation.Parameters ??= [];
                    if (operation.Parameters.Any(p => p.In == ParameterLocation.Path && p.Name == name))
                    {
                        continue;
                    }

                    var isGuid = pattern?.GetParameter(name)?.ParameterPolicies.Any(p => p.Content == "guid") == true;
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name,
                        In = ParameterLocation.Path,
                        Required = true,
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Format = isGuid ? "uuid" : null,
                        },
                    });
                }

                return Task.CompletedTask;
            });

            // Every operation can fail with RFC 9457 ProblemDetails (CLAUDE.md rule 4).
            options.AddOperationTransformer((operation, _, _) =>
            {
                operation.Responses ??= [];
                // TryAdd keeps endpoint-specific responses (e.g. 503 on /api/health/ready).
                operation.Responses.TryAdd("default", new OpenApiResponse
                {
                    Description = "Error (RFC 9457 ProblemDetails)",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference(ProblemDetailsName) },
                    },
                });
                return Task.CompletedTask;
            });
        });
        return services;
    }

    private static IEnumerable<string> TemplateParameters(string relativePath) =>
        ParameterName().Matches(relativePath).Select(m => m.Groups[1].Value).Distinct();

    private static RoutePattern? FindPattern(OpenApiOperationTransformerContext context, string relativePath)
    {
        var dataSource = context.ApplicationServices.GetService<EndpointDataSource>();
        var wanted = WithoutConstraints().Replace("/" + relativePath.TrimStart('/'), "{$1}");
        return dataSource?.Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern)
            .FirstOrDefault(p => p.RawText is { } raw && WithoutConstraints().Replace(raw, "{$1}") == wanted);
    }

    [GeneratedRegex(@"\{([^}:?=*]+)")]
    private static partial Regex ParameterName();

    [GeneratedRegex(@"\{([^}:?=*]+)[^}]*\}")]
    private static partial Regex WithoutConstraints();
}
