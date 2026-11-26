using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace Cluckwork.Api.IntegrationTests;

// #1285 G8.9 (record 1282 decision 13): the passkey sign-in options route's
// per-address policy, and auth-login, at their shipped limits, in a host of
// their own so no other test spends the budgets.
public sealed class PasskeyOptionsRateLimitFactory : CluckworkWebApplicationFactory
{
    public SecondFactorFactory.LogSink Sink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var shipped = new RateLimitingOptions();
        // The base factory lifts only the permit limits; the windows keep their defaults.
        builder.UseSetting("RateLimiting:PasskeyOptions:PermitLimit", shipped.PasskeyOptions.PermitLimit.ToString());
        builder.UseSetting("RateLimiting:Login:PermitLimit", shipped.Login.PermitLimit.ToString());
        builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(Sink));
    }

    public bool Rejected(string policy) => Sink.Events.Any(e =>
        e.Properties.TryGetValue("SecurityEvent", out var name) && name is ScalarValue { Value: SecurityEvents.RateLimitRejected }
        && e.Properties["Policy"] is ScalarValue { Value: var value } && Equals(value, policy));
}

public sealed class PasskeyOptionsRateLimitTests(PasskeyOptionsRateLimitFactory factory) : IClassFixture<PasskeyOptionsRateLimitFactory>
{
    private const string OptionsPath = "/api/v1/auth/login/passkey/options";
    private const string SignInPath = "/api/v1/auth/login/passkey";

    // Passkeys are off in this host, so the routes answer 404; the limiter
    // runs before them, so each request still spends its permit.
    [Fact]
    public async Task TheSixtyFirstOptionsRequest_IsRefused_AndASignInSpendsALoginPermit()
    {
        var client = factory.CreateClient();
        var signIn = new { ceremony = Guid.NewGuid(), credential = new { id = "AQID" } };

        for (var i = 0; i < 60; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.PostAsync(OptionsPath, null)).StatusCode);
        var refused = await client.PostAsync(OptionsPath, null);
        for (var i = 0; i < 10; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync(SignInPath, signIn)).StatusCode);
        var signInRefused = await client.PostAsJsonAsync(SignInPath, signIn);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(factory.Rejected(RateLimitingOptions.PasskeyOptionsPolicyName), "the options refusal logged no Auth.RateLimitRejected");
        Assert.Equal(HttpStatusCode.TooManyRequests, signInRefused.StatusCode);
        Assert.True(factory.Rejected(RateLimitingOptions.LoginPolicyName), "the sign-in refusal logged no auth-login rejection");
    }
}
