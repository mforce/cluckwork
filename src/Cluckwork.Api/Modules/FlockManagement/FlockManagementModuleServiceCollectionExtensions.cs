using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Application.Modules.FlockManagement.Flocks;
using Cluckwork.Application.Modules.FlockManagement.Flocks.ArchiveFlock;
using Cluckwork.Application.Modules.FlockManagement.Flocks.CreateFlock;
using Cluckwork.Application.Modules.FlockManagement.Flocks.DepleteFlock;
using Cluckwork.Application.Modules.FlockManagement.Flocks.ReactivateFlock;
using Cluckwork.Application.Modules.FlockManagement.Flocks.RecordBirdMovement;
using Cluckwork.Application.Modules.FlockManagement.Flocks.UpdateFlock;
using Cluckwork.Infrastructure.Repositories;
using FluentValidation;

namespace Cluckwork.Api.Modules.FlockManagement;

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
