namespace Cluckwork.Api.Hosting;

using Cluckwork.Api.Configuration;
using Cluckwork.Infrastructure.SharedState;

// #543 — binds SharedStateOptions and delegates to the Infrastructure
// registration helper. failOnMalformed is serving-only: a one-shot verb must
// not be aborted by a bad SharedState connection string it never uses (#347).
internal static class CluckworkSharedStateServiceCollectionExtensions
{
    public static void AddCluckworkSharedState(
        this IServiceCollection services, IConfiguration configuration, ProcessRole role)
    {
        var options = configuration.GetSection(SharedStateOptions.SectionName)
            .Get<SharedStateOptions>() ?? new SharedStateOptions();

        services.AddCluckworkSharedState(
            options.Redis.ConnectionString,
            options.Redis.KeyNamespace,
            failOnMalformed: role is ProcessRole.Serving);
    }
}
