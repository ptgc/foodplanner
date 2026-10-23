using Foodplanner.Api.Problems;
using Microsoft.AspNetCore.Antiforgery;

namespace Foodplanner.Api.Security;

/// <summary>Endpoint metadata proving an endpoint hangs off the antiforgery-validating root group.</summary>
public sealed class AntiforgeryValidatedMetadata
{
    public static AntiforgeryValidatedMetadata Instance { get; } = new();

    private AntiforgeryValidatedMetadata()
    {
    }
}

public static class ApiRoot
{
    public const string HeaderName = "X-XSRF-TOKEN";
    public const string CookieName = "__Host-foodplanner-xsrf";

    /// <summary>
    /// The one root group every endpoint hangs off (<c>/api</c>). Its filter validates the antiforgery token on every
    /// POST, PUT, PATCH and DELETE, including login and register. Never map an endpoint on the app directly: the
    /// test All_unsafe_endpoints_validate_antiforgery fails for an unsafe endpoint without this group's metadata.
    /// </summary>
    public static RouteGroupBuilder MapApiRoot(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.WithMetadata(AntiforgeryValidatedMetadata.Instance);
        api.AddEndpointFilter<AntiforgeryEndpointFilter>();
        return api;
    }

    private sealed class AntiforgeryEndpointFilter(IAntiforgery antiforgery) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
                || HttpMethods.IsOptions(http.Request.Method) || HttpMethods.IsTrace(http.Request.Method))
            {
                return await next(context);
            }

            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return AppProblems.Problem(
                    StatusCodes.Status400BadRequest,
                    AppProblems.AntiforgeryInvalid,
                    "Invalid antiforgery token",
                    "Fetch a fresh token from GET /api/auth/antiforgery and send it in the X-XSRF-TOKEN header.");
            }

            return await next(context);
        }
    }
}
