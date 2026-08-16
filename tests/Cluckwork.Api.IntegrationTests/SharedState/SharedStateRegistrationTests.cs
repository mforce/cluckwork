namespace Cluckwork.Api.IntegrationTests.SharedState;

using Cluckwork.Infrastructure.SharedState;
using Microsoft.Extensions.DependencyInjection;

public sealed class SharedStateRegistrationTests
{
    private static ServiceProvider Build(string? conn, bool failOnMalformed)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        services.AddCluckworkSharedState(conn, "test-ns", failOnMalformed);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void BlankConnectionString_RegistersInProcessImplementations()
    {
        using var sp = Build("", failOnMalformed: true);
        Assert.IsType<InProcessClaimOnceStore>(sp.GetRequiredService<IClaimOnceStore>());
        Assert.IsType<InProcessFixedWindowCounter>(sp.GetRequiredService<IFixedWindowCounter>());
        Assert.IsType<InProcessLease>(sp.GetRequiredService<ILease>());
    }

    [Fact]
    public void ConfiguredConnectionString_RegistersResilientDecorators()
    {
        // A well-formed endpoint. AbortOnConnectFail=false means resolving these
        // does NOT require a reachable Redis — the multiplexer connects lazily.
        using var sp = Build("localhost:6399", failOnMalformed: true);
        Assert.IsType<ResilientClaimOnceStore>(sp.GetRequiredService<IClaimOnceStore>());
        Assert.IsType<ResilientFixedWindowCounter>(sp.GetRequiredService<IFixedWindowCounter>());
        Assert.IsType<ResilientLease>(sp.GetRequiredService<ILease>());
    }

    [Fact]
    public void MalformedConnectionString_Serving_Throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        // Options but no endpoint -> malformed.
        Assert.Throws<InvalidOperationException>(() =>
            services.AddCluckworkSharedState("abortConnect=false", "test-ns", failOnMalformed: true));
    }

    [Fact]
    public void MalformedConnectionString_OneShot_DegradesToInProcess()
    {
        using var sp = Build("abortConnect=false", failOnMalformed: false);
        Assert.IsType<InProcessClaimOnceStore>(sp.GetRequiredService<IClaimOnceStore>());
    }
}
