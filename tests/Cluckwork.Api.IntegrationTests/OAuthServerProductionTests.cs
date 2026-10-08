using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Cluckwork.Api.IntegrationTests;

public sealed class OAuthProductionFactory : CluckworkWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
        builder.UseSetting("AllowedHosts", "localhost");
    }
}

// #795 — Production cannot issue an OAuth token until client registration (#797) and
// consent with step-up (#798) exist. The base factory configures an issuer, so this
// host proves the environment gate holds even when the issuer is set.
public sealed class OAuthServerProductionTests(OAuthProductionFactory factory)
    : IClassFixture<OAuthProductionFactory>
{
    [Fact]
    public async Task Production_RunsNoAuthorizationServer()
    {
        var client = OAuthServerTests.HttpsClient(factory, bearer: null);

        using var token = await client.PostAsync("/api/v1/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["grant_type"] = "authorization_code" }));
        using var authorize = await client.GetAsync("/api/v1/oauth/authorize?response_type=code");
        using var metadata = await client.GetAsync("/.well-known/oauth-authorization-server");

        Assert.Equal(HttpStatusCode.NotFound, token.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, authorize.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        Assert.Null(factory.Services.GetService<IOpenIddictApplicationManager>());
    }
}
