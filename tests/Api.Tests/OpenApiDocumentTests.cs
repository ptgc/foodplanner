using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Foodplanner.Api.Tests;

/// <summary>
/// The committed document (openapi/openapi.v1.json, input for the TS client in T-004) must be valid,
/// describe the health endpoint and ProblemDetails, and equal what the Api generates.
/// To regenerate after an API change: scripts/openapi.sh (or scripts/openapi.ps1).
/// </summary>
public class OpenApiDocumentTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string DocumentUrl = "/openapi/v1.json";

    private static string CommittedPath => Path.Combine(RepoRoot(), "openapi", "openapi.v1.json");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foodplanner.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Foodplanner.slnx not found above test output.");
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";

    private async Task<string> GenerateAsync(string environment)
    {
        using var client = factory.WithWebHostBuilder(b => b.UseEnvironment(environment)).CreateClient();
        using var response = await client.GetAsync(DocumentUrl, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Normalize(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Returns the problems found; empty means the document is valid and complete.</summary>
    internal static List<string> Validate(string json)
    {
        var problems = new List<string>();
        OpenApiDocument? document;
        OpenApiDiagnostic? diagnostic;
        try
        {
            (document, diagnostic) = OpenApiDocument.Parse(json, "json");
        }
        catch (Exception ex)
        {
            return [$"Unparseable: {ex.Message}"];
        }

        if (document is null || diagnostic is null)
        {
            return ["Document could not be read."];
        }

        problems.AddRange(diagnostic.Errors.Select(e => $"{e.Pointer}: {e.Message}"));

        var health = document.Paths is { } paths && paths.TryGetValue("/api/health", out var item)
            && item.Operations is { } ops && ops.TryGetValue(HttpMethod.Get, out var op) ? op : null;
        if (health is null)
        {
            problems.Add("GET /api/health is missing.");
            return problems;
        }

        if (health.Responses?.ContainsKey("200") != true)
        {
            problems.Add("GET /api/health has no 200 response.");
        }

        if (health.Responses is not { } responses
            || !responses.TryGetValue("default", out var error)
            || error.Content is not { } content
            || !content.TryGetValue("application/problem+json", out var errorMedia)
            || errorMedia.Schema is null)
        {
            problems.Add("GET /api/health has no default application/problem+json response.");
        }

        var ready = document.Paths.TryGetValue("/api/health/ready", out var readyItem)
            && readyItem.Operations is { } readyOps && readyOps.TryGetValue(HttpMethod.Get, out var readyOp) ? readyOp : null;
        if (ready?.Responses is not { } readyResponses
            || !readyResponses.ContainsKey("200")
            || !readyResponses.TryGetValue("503", out var unavailable)
            || unavailable.Content?.ContainsKey("application/problem+json") != true)
        {
            problems.Add("GET /api/health/ready must describe 200 and a 503 application/problem+json response.");
        }

        if (document.Components?.Schemas?.ContainsKey("ProblemDetails") != true)
        {
            problems.Add("Components has no ProblemDetails schema.");
        }

        return problems;
    }

    [Fact]
    public async Task Generated_document_is_valid_and_matches_committed_file()
    {
        var generated = await GenerateAsync("Development");

        if (Environment.GetEnvironmentVariable("OPENAPI_UPDATE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CommittedPath)!);
            await File.WriteAllTextAsync(CommittedPath, generated, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        }

        Assert.Empty(Validate(generated));

        Assert.True(File.Exists(CommittedPath), $"{CommittedPath} missing. Run scripts/openapi.sh");
        var committed = Normalize(await File.ReadAllTextAsync(CommittedPath, TestContext.Current.CancellationToken));
        Assert.True(committed == generated, "openapi/openapi.v1.json is out of date. Run scripts/openapi.sh and commit the result.");
    }

    [Fact]
    public async Task Every_path_template_parameter_is_declared_and_vice_versa()
    {
        var generated = await GenerateAsync("Development");
        var (document, _) = OpenApiDocument.Parse(generated, "json");
        Assert.NotNull(document);

        var problems = new List<string>();
        foreach (var (path, item) in document.Paths)
        {
            var inTemplate = System.Text.RegularExpressions.Regex.Matches(path, @"\{([^}]+)\}")
                .Select(m => m.Groups[1].Value).ToHashSet();
            foreach (var (method, operation) in item.Operations ?? [])
            {
                var declared = (operation.Parameters ?? []).Where(p => p.In == ParameterLocation.Path).ToList();
                problems.AddRange(inTemplate.Where(n => declared.All(p => p.Name != n))
                    .Select(n => $"{method} {path}: {{{n}}} is not declared as a path parameter."));
                problems.AddRange(declared.Where(p => !inTemplate.Contains(p.Name!))
                    .Select(p => $"{method} {path}: path parameter {p.Name} is not in the template."));
                problems.AddRange(declared.Where(p => p.Required != true)
                    .Select(p => $"{method} {path}: path parameter {p.Name} is not required."));
                problems.AddRange(declared.Where(p => p.Name is "householdId" or "recipeId" or "entryId" or "itemId"
                        && p.Schema?.Format != "uuid")
                    .Select(p => $"{method} {path}: {p.Name} should be a uuid."));
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public async Task Committed_document_is_valid()
    {
        var committed = await File.ReadAllTextAsync(CommittedPath, TestContext.Current.CancellationToken);

        Assert.Empty(Validate(committed));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"openapi":"3.1.0","info":{"title":"x"},"paths":{}}""")]
    [InlineData("""{"openapi":"3.1.0","info":{"title":"x","version":"1"},"paths":{}}""")]
    public void Validator_rejects_invalid_or_incomplete_documents(string json)
    {
        Assert.NotEmpty(Validate(json));
    }

    [Fact]
    public async Task Openapi_endpoint_is_served_in_Development()
    {
        using var client = factory.WithWebHostBuilder(b => b.UseEnvironment("Development")).CreateClient();

        using var response = await client.GetAsync(DocumentUrl, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Openapi_endpoint_is_not_served_in_Production()
    {
        // Outside Development the email provider must be set explicitly (T-009), otherwise startup fails.
        using var client = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Email:Provider", "Recording");
        }).CreateClient();

        using var response = await client.GetAsync(DocumentUrl, TestContext.Current.CancellationToken);

        // The route does not exist. An anonymous caller gets 401 rather than 404, because the fallback authorization
        // policy (T-009) also covers unmatched paths; either way nothing is served.
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Unauthorized });
        Assert.NotEqual("application/json", response.Content.Headers.ContentType?.MediaType);
    }
}
