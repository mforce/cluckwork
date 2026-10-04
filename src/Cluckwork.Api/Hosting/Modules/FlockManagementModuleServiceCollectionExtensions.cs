using Cluckwork.Application.Features.Flocks;
using Cluckwork.Application.Features.Flocks.ArchiveFlock;
using Cluckwork.Application.Features.Flocks.CreateFlock;
using Cluckwork.Application.Features.Flocks.DepleteFlock;
using Cluckwork.Application.Features.Flocks.ReactivateFlock;
using Cluckwork.Application.Features.Flocks.RecordBirdMovement;
using Cluckwork.Application.Features.Flocks.UpdateFlock;
using Cluckwork.Infrastructure.Repositories;
using FluentValidation;

namespace Cluckwork.Api.Hosting.Modules;

internal static class FlockManagementModuleServiceCollectionExtensions
{
    public static IServiceCollection AddFlockManagementModule(this IServiceCollection services)
    {
        services.AddScoped<IFlockRepository, FlockRepository>();
        services.AddScoped<IBirdMovementRepository, BirdMovementRepository>();
        services.AddScoped<IValidator<CreateFlockCommand>, CreateFlockValidator>();
        services.AddScoped<IValidator<UpdateFlockCommand>, UpdateFlockValidator>();
        services.AddScoped<
            IValidator<RecordBirdMovementCommand>,
            RecordBirdMovementValidator>();
        services.AddScoped<CreateFlockHandler>();
        services.AddScoped<DepleteFlockHandler>();
        services.AddScoped<UpdateFlockHandler>();
        services.AddScoped<ArchiveFlockHandler>();
        services.AddScoped<RecordBirdMovementHandler>();
        services.AddScoped<ReactivateFlockHandler>();
        services.AddScoped<IFlockModule, FlockModule>();
        services.AddScoped<IFlockLookup, FlockLookup>();
        services.AddScoped<IMortalityLedger, MortalityLedger>();

        return services;
    }
}
