using Cluckwork.Api.Middleware;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Repositories;
using Cluckwork.Infrastructure.Time;
using FluentValidation;

namespace Cluckwork.Api.Hosting.Modules;

internal static class CluckworkPlatformServiceCollectionExtensions
{
    public static IServiceCollection AddCluckworkPlatformServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IFlockScopeGuard, FlockScopeGuard>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ICurrentTransaction, CurrentTransaction>();
        services.AddScoped<IClock, SystemClock>();
        services.AddScoped<IFarmClock, FarmClock>();
        services.AddSingleton(TimeProvider.System);
        // #309 — the login DTO validator lives in the Api assembly (it validates
        // the Api LoginRequest). MAX-length only; see LoginRequestValidator.
        services.AddScoped<
            IValidator<Cluckwork.Api.Modules.Access.Auth.LoginRequest>,
            Cluckwork.Api.Modules.Access.Auth.LoginRequestValidator>();
        // #308
        services.AddScoped<
            IValidator<Cluckwork.Api.Modules.Access.Auth.StepUpRequest>,
            Cluckwork.Api.Modules.Access.Auth.StepUpRequestValidator>();

        // #307 — lease duration / max-wait bounds for the idempotency claim protocol.
        services.Configure<IdempotencyOptions>(
            configuration.GetSection(IdempotencyOptions.SectionName));

        return services;
    }
}
