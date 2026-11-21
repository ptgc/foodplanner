using Foodplanner.Infrastructure.Oda;

namespace Foodplanner.Api.Tests.Oda;

/// <summary>T-115: the return address is https everywhere except a local development host.</summary>
public sealed class OdaOriginTests
{
    [Theory]
    [InlineData("http://localhost:3000", true)]
    [InlineData("http://127.0.0.1:3000", true)]
    [InlineData("http://[::1]:3000", true)]
    [InlineData("https://app.example.com", true)]
    [InlineData("https://localhost:3000", true)]
    [InlineData("http://app.example.com", false)]
    [InlineData("http://localhost.example.com", false)]
    [InlineData("ftp://localhost", false)]
    [InlineData("not a url", false)]
    public void Only_https_or_a_local_host_is_accepted(string origin, bool accepted)
    {
        var options = new OdaOptions { ClientId = "client", WebOrigin = origin };

        Assert.Equal(accepted, options.IsConfigured);
        Assert.Equal(accepted, options.RedirectUri is not null);
        Assert.Equal(!accepted, options.HasInsecureOrigin);
    }

    [Fact]
    public void A_missing_origin_or_client_id_is_not_flagged_as_insecure()
    {
        Assert.False(new OdaOptions { ClientId = "client" }.HasInsecureOrigin);
        Assert.False(new OdaOptions { WebOrigin = "http://app.example.com" }.HasInsecureOrigin);
    }
}
