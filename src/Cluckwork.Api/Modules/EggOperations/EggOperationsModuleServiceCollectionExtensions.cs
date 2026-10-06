using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Application.Modules.EggOperations.DailyEntries;
using Cluckwork.Application.Modules.EggOperations.DailyEntries.AdjustDailyEntry;
using Cluckwork.Application.Modules.EggOperations.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Modules.EggOperations.DailyEntries.SubmitDailyEntry;
using Cluckwork.Application.Modules.EggOperations.DailyEntries.VoidDailyEntry;
using Cluckwork.Application.Modules.EggOperations.EggGrades;
using Cluckwork.Application.Modules.EggOperations.EggGrades.CreateEggGrade;
using Cluckwork.Application.Modules.EggOperations.EggGrades.SetEggGradeActive;
using Cluckwork.Application.Modules.EggOperations.EggGrades.UpdateEggGrade;
using Cluckwork.Application.Modules.EggOperations.EggLots;
using Cluckwork.Application.Modules.EggOperations.EggLots.RecordEggLotMovement;
using Cluckwork.Infrastructure.Modules.EggOperations.Repositories;
using FluentValidation;

namespace Cluckwork.Api.Modules.EggOperations;

internal static class EggOperationsModuleServiceCollectionExtensions
{
    public static IServiceCollection AddEggOperationsModule(this IServiceCollection services)
    {
        services.AddScoped<
            Cluckwork.Application.Modules.EggOperations.Eggs.IEggInventoryMovementRepository,
            EggInventoryMovementRepository>();
        services.AddScoped<IDailyEntryRepository, DailyEntryRepository>();
        services.AddScoped<IEggLotRepository, EggLotRepository>();
        services.AddScoped<IEggGradeRepository, EggGradeRepository>();
        services.AddScoped<
            IValidator<RecordDailyEntryCommand>,
            RecordDailyEntryValidator>();
        services.AddScoped<
            IValidator<CreateEggGradeCommand>,
            CreateEggGradeValidator>();
        services.AddScoped<
            IValidator<UpdateEggGradeCommand>,
            UpdateEggGradeValidator>();
        services.AddScoped<
            IValidator<RecordEggLotMovementCommand>,
            RecordEggLotMovementValidator>();
        services.AddScoped<
            IValidator<AdjustDailyEntryCommand>,
            AdjustDailyEntryValidator>();
        services.AddScoped<
            IValidator<VoidDailyEntryCommand>,
            VoidDailyEntryValidator>();
        services.AddScoped<RecordDailyEntryHandler>();
        services.AddScoped<SubmitDailyEntryHandler>();
        services.AddScoped<RecordEggLotMovementHandler>();
        services.AddScoped<CreateEggGradeHandler>();
        services.AddScoped<UpdateEggGradeHandler>();
        services.AddScoped<SetEggGradeActiveHandler>();
        services.AddScoped<AdjustDailyEntryHandler>();
        services.AddScoped<VoidDailyEntryHandler>();
        services.AddScoped<
            Cluckwork.Application.Modules.EggOperations.DailyEntries.LockDueDailyEntries.LockDueDailyEntriesHandler>();
        services.AddScoped<
            Cluckwork.Application.Modules.EggOperations.Contracts.IEggOperationsModule,
            Cluckwork.Application.Modules.EggOperations.Eggs.EggOperationsModule>();
        services.AddScoped<IEggGradeLookup, EggGradeLookup>();
        services.AddScoped<IEggStock, EggStock>();
        services.AddScoped<IEggGradeProvisioning, EggGradeProvisioning>();
        services.AddScoped<IDailyEntryLookup, DailyEntryLookup>();

        return services;
    }
}
