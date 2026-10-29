using System.Net;
using System.Runtime.CompilerServices;
using Foodplanner.Api.Tests.Auth;

namespace Foodplanner.Api.Tests;

/// <summary>T-098 regression: a disposed test host must be garbage collectible, otherwise Api.Tests memory grows with the number of tests.</summary>
public sealed class HostCleanupTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> UseAndDisposeHostAsync()
    {
        var host = AuthHost.Create(connectionString: string.Empty);
        using (var client = host.CreateClient())
        {
            using var response = await client.GetAsync("/api/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var weak = new WeakReference(host.Server);
        await host.DisposeAsync();
        return weak;
    }

    [Fact]
    public async Task Disposed_host_is_released_by_the_garbage_collector()
    {
        var weak = await Task.Run(UseAndDisposeHostAsync, TestContext.Current.CancellationToken);

        for (var attempt = 0; attempt < 10 && weak.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.False(weak.IsAlive, "the test server is still reachable after its host was disposed (see HostCleanup)");
    }

    [Fact]
    public async Task Rate_limiters_of_a_started_host_are_found_and_disposed()
    {
        await using var host = AuthHost.Create(connectionString: string.Empty);
        using var client = host.CreateClient();
        using var response = await client.GetAsync("/api/health");

        // Finding the middleware is by reflection: if a framework update renames its fields this returns 0.
        Assert.True(HostCleanup.DisposeRateLimiters(host.Server) >= 1);
    }
}
