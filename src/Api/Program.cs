using Foodplanner.Api.Features.Health;
using Foodplanner.Api.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFoodplannerOpenApi();

var app = builder.Build();

// Runtime exposure of /openapi/v1.json is Development only.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthEndpoints();

app.Run();

// Makes Program visible to WebApplicationFactory<Program> in Api.Tests.
public partial class Program;
