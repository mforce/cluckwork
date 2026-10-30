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

// #1278 G3.10: the code step's per-address policy at its shipped limit, in a
// host of its own so no other test spends the budget.
public sealed class SecondFactorRateLimitFactory : CluckworkWebApplicationFactory
{
    public SecondFactorFactory.LogSink Sink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var shipped = new RateLimitingOptions().SecondFactor;
        builder.UseSetting("RateLimiting:SecondFactor:PermitLimit", shipped.PermitLimit.ToString());
        builder.UseSetting("RateLimiting:SecondFactor:WindowSeconds", shipped.WindowSeconds.ToString());
        builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(Sink));
    }
}

public sealed class SecondFactorRateLimitTests(SecondFactorRateLimitFactory factory) : IClassFixture<SecondFactorRateLimitFactory>
{
    [Fact]
    public async Task TheTwentyFirstCodeFromOneAddress_IsRefused_AndLogged()
    {
        Assert.Equal((20, 900), (new RateLimitingOptions().SecondFactor.PermitLimit, new RateLimitingOptions().SecondFactor.WindowSeconds));
        var client = factory.CreateClient();
        var body = new { challenge = "not-a-challenge", code = "123456" };

        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(TestHarness.SecondFactorPath, body)).StatusCode);
        var refused = await client.PostAsJsonAsync(TestHarness.SecondFactorPath, body);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains(factory.Sink.Events, e =>
            e.Properties.TryGetValue("SecurityEvent", out var name) && name is ScalarValue { Value: SecurityEvents.RateLimitRejected }
            && e.Properties["Policy"] is ScalarValue { Value: RateLimitingOptions.SecondFactorPolicyName });
    }

    [Fact]
    public void AZeroSecondFactorLimit_IsRejectedAtBoot() =>
        Assert.Throws<InvalidOperationException>(new RateLimitingOptions
        {
            SecondFactor = new RateLimitingOptions.FixedWindow { PermitLimit = 0, WindowSeconds = 900 }
        }.Validate);
}
