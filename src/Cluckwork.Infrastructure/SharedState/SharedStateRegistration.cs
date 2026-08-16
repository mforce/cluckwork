namespace Cluckwork.Infrastructure.SharedState;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

// #543 — registers the three shared-state ports. Blank connection string =>
// in-process implementations (Option B). A configured connection string =>
// Redis primary wrapped in the resilient decorators (in-process fallback),
// with AbortOnConnectFail=false so an UNREACHABLE Redis degrades at runtime
// via the decorators rather than failing the boot. A MALFORMED connection
// string throws only when failOnMalformed is true (the serving process); a
// one-shot verb passes false and degrades to in-process with a stderr warning
// (same shape as AddCluckworkRateLimiting).
public static class SharedStateRegistration
{
    public static void AddCluckworkSharedState(
        this IServiceCollection services,
        string? connectionString,
        string keyNamespace,
        bool failOnMalformed)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            RegisterInProcess(services);
            return;
        }

        ConfigurationOptions parsed;
        try
        {
            parsed = ConfigurationOptions.Parse(connectionString);
        }
        catch (ArgumentException ex)
        {
            HandleMalformed(services, failOnMalformed, ex.Message, ex);
            return;
        }

        if (parsed.EndPoints.Count == 0)
        {
            HandleMalformed(services, failOnMalformed,
                "no endpoint (host:port) was found in the value", inner: null);
            return;
        }

        // Unreachable Redis must DEGRADE at runtime (the resilient decorators
        // fall back), never fail the boot — so do not abort the connect.
        parsed.AbortOnConnectFail = false;

        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(parsed));

        // Grant replay: fail-closed decorator, NO in-process fallback.
        services.AddSingleton<IClaimOnceStore>(sp =>
            new ResilientClaimOnceStore(
                new RedisClaimOnceStore(sp.GetRequiredService<IConnectionMultiplexer>(), keyNamespace),
                sp.GetRequiredService<ILogger<ResilientClaimOnceStore>>()));

        // Auth limiter + report lease: fall-back decorators over an in-process fallback.
        services.AddSingleton<IFixedWindowCounter>(sp =>
            new ResilientFixedWindowCounter(
                new RedisFixedWindowCounter(sp.GetRequiredService<IConnectionMultiplexer>(), keyNamespace),
                new InProcessFixedWindowCounter(sp.GetRequiredService<TimeProvider>()),
                sp.GetRequiredService<ILogger<ResilientFixedWindowCounter>>()));

        services.AddSingleton<ILease>(sp =>
            new ResilientLease(
                new RedisLease(sp.GetRequiredService<IConnectionMultiplexer>(), keyNamespace),
                new InProcessLease(sp.GetRequiredService<TimeProvider>()),
                sp.GetRequiredService<ILogger<ResilientLease>>()));
    }

    private static void HandleMalformed(
        IServiceCollection services, bool failOnMalformed, string reason, Exception? inner)
    {
        if (failOnMalformed)
            throw new InvalidOperationException(
                "SharedState:Redis:ConnectionString is set but not a valid StackExchange.Redis "
                + $"connection string: {reason}. Fix the value, or leave it blank to run "
                + "single-instance on the in-process implementations.", inner);

        Console.Error.WriteLine(
            $"warning: SharedState Redis not configured for this command — {reason}");
        RegisterInProcess(services);
    }

    private static void RegisterInProcess(IServiceCollection services)
    {
        services.AddSingleton<IClaimOnceStore>(sp =>
            new InProcessClaimOnceStore(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IFixedWindowCounter>(sp =>
            new InProcessFixedWindowCounter(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ILease>(sp =>
            new InProcessLease(sp.GetRequiredService<TimeProvider>()));
    }
}
