using System.Reflection;

namespace Foodplanner.Architecture.Tests;

public class DependencyRulesTests
{
    private static string[] ReferencedNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

    [Fact]
    public void Domain_does_not_reference_EfCore_or_AspNetCore()
    {
        var offenders = ReferencedNames(typeof(Foodplanner.Domain.AssemblyMarker).Assembly)
            .Where(n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                     || n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Domain_project_file_has_no_EfCore_or_AspNetCore_references()
    {
        // GetReferencedAssemblies omits references the compiler found unused, so also check the project file.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foodplanner.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var csproj = File.ReadAllText(Path.Combine(dir.FullName, "src", "Domain", "Foodplanner.Domain.csproj"));

        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.AspNetCore", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Infrastructure_does_not_reference_Api()
    {
        var offenders = ReferencedNames(typeof(Foodplanner.Infrastructure.AssemblyMarker).Assembly)
            .Where(n => n == "Foodplanner.Api")
            .ToArray();

        Assert.Empty(offenders);
    }
}
