using Foodplanner.Api.Features.Health;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapHealthEndpoints();

app.Run();

// Makes Program visible to WebApplicationFactory<Program> in Api.Tests.
public partial class Program;
