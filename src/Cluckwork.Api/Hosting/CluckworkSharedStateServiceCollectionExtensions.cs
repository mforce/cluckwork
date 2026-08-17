namespace Cluckwork.Api.Hosting;

using Cluckwork.Api.Configuration;
using Cluckwork.Infrastructure.SharedState;

// #543 — binds SharedStateOptions, runs the serving-only connection-string
// guard, then delegates registration to Infrastructure.
internal static class CluckworkSharedStateServiceCollectionExtensions
{
    public static void AddCluckworkSharedState(
        this IServiceCollection services, IConfiguration configuration, ProcessRole role)
    {
        var options = configuration.GetSection(SharedStateOptions.SectionName)
            .Get<SharedStateOptions>() ?? new SharedStateOptions();

        EnsureSharedStateConnectionValid(options.Redis.ConnectionString, role);

        // The guard above owns the serving-fail, so the helper never needs to
        // throw — a one-shot verb with a bad string degrades to in-process.
        services.AddCluckworkSharedState(
            options.Redis.ConnectionString,
            options.Redis.KeyNamespace,
            failOnMalformed: false);
    }

    // #543/#347 — SERVING-ONLY boot guard: a serving process with a set-but-
    // malformed Redis connection string must fail loudly rather than silently
    // run single-instance on the in-process fallback. Blank is fine (Option B,
    // deliberate single-instance). A one-shot verb (migrate/seed/…) never uses
    // shared state, so it is skipped — making this eager for every role would be
    // a fresh #331. Discovered by ServingGuardCoverageTests (an `Ensure*` method
    // in this namespace); its ProcessRoleGuardTests row proves it fires.
    private static void EnsureSharedStateConnectionValid(string? connectionString, ProcessRole role)
    {
        if (role is not ProcessRole.Serving)
            return;
        if (string.IsNullOrWhiteSpace(connectionString))
            return;
        if (SharedStateRegistration.IsWellFormedConnectionString(connectionString))
            return;

        throw new InvalidOperationException(
            $"{SharedStateRegistration.MalformedConnectionStringMessagePrefix} (no endpoint "
            + "host:port was found). Fix the value, or leave it blank to run single-instance "
            + "on the in-process implementations.");
    }
}
