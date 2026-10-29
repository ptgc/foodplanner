using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

[assembly: AssemblyFixture(typeof(Foodplanner.Api.Tests.TestProcessSetup))]

namespace Foodplanner.Api.Tests;

/// <summary>
/// T-098: runs once before any test of the assembly. Turns off reload-on-change of the appsettings files for every test host
/// (<c>hostBuilder:reloadConfigOnChange</c>). Each host otherwise starts a FileSystemWatcher with a pending overlapped I/O that
/// captures the creating thread's ExecutionContext, and that keeps the whole disposed host reachable (see <see cref="HostCleanup"/>).
/// </summary>
public sealed class TestProcessSetup
{
    public TestProcessSetup() =>
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
}

/// <summary>
/// T-098: stops the timer that ASP.NET Core's rate limiter middleware leaves running after a host is disposed.
/// <para>
/// The middleware builds a <c>PartitionedRateLimiter</c> (an idle-partition cleanup timer on the static timer queue) when the pipeline
/// is built, and nothing disposes it when the host stops: the middleware is not <see cref="IDisposable"/> and is not in the DI container.
/// The timer callback references the limiter, the limiter's partitioner references the middleware, and the middleware references the
/// rest of the pipeline, so every disposed test host (about 3 MB of managed heap: DI containers, EF model, endpoints, JIT-ed
/// dynamic methods) stayed reachable for the life of the process. Measured in T-098: one retained host per test that creates one,
/// about 1.6 GB after the whole Api.Tests run. In production there is one host per process, so the app itself does not leak.
/// </para>
/// <para>
/// There is no public way to reach the limiter, so this walks the delegate chain of the pipeline by reflection (test code only).
/// If a framework update renames the private fields, the walk finds nothing and the memory growth comes back;
/// <c>RateLimiterTimerTests</c> fails then.
/// </para>
/// </summary>
internal static class HostCleanup
{
    private const BindingFlags AllFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private const string MiddlewareType = "Microsoft.AspNetCore.RateLimiting.RateLimitingMiddleware";

    /// <summary>Disposes the rate limiters of the middleware found from <paramref name="server"/> (a TestServer). Returns how many were disposed.</summary>
    public static int DisposeRateLimiters(object server)
    {
        var middleware = FindRateLimitingMiddleware(server);
        if (middleware is null)
        {
            return 0;
        }

        var disposed = 0;
        for (var type = middleware.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(AllFields))
            {
                if (field.GetValue(middleware) is PartitionedRateLimiter<HttpContext> limiter)
                {
                    limiter.Dispose();
                    disposed++;
                }
            }
        }

        return disposed;
    }

    private static object? FindRateLimitingMiddleware(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        queue.Enqueue(root);
        while (queue.Count > 0 && seen.Count < 20_000)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            if (current.GetType().FullName == MiddlewareType)
            {
                return current;
            }

            if (current is Delegate del)
            {
                if (del.Target is { } target)
                {
                    queue.Enqueue(target);
                }

                continue;
            }

            for (var type = current.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(AllFields))
                {
                    if (field.FieldType.IsValueType || field.GetValue(current) is not { } value)
                    {
                        continue;
                    }

                    if (value is Delegate || IsPipelineType(value.GetType()))
                    {
                        queue.Enqueue(value);
                    }
                }
            }
        }

        return null;
    }

    // Pipeline objects only (TestServer, hosting application, middleware, compiler-generated closures of UseExtensions);
    // not the DI container, configuration or the application's services.
    private static bool IsPipelineType(Type type) =>
        type.Namespace is { } ns
        && (ns.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) || ns.StartsWith("Foodplanner.Api", StringComparison.Ordinal))
        && !typeof(IServiceProvider).IsAssignableFrom(type);
}
