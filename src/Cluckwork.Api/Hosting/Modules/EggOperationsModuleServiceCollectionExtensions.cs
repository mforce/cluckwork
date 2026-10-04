using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Application.Features.DailyEntries.AdjustDailyEntry;
using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;
using Cluckwork.Application.Features.DailyEntries.VoidDailyEntry;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.EggGrades.CreateEggGrade;
using Cluckwork.Application.Features.EggGrades.SetEggGradeActive;
using Cluckwork.Application.Features.EggGrades.UpdateEggGrade;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.EggLots.RecordEggLotMovement;
using Cluckwork.Infrastructure.Repositories;
using FluentValidation;

namespace Cluckwork.Api.Hosting.Modules;

internal static class EggOperationsModuleServiceCollectionExtensions
{
    public static IServiceCollection AddEggOperationsModule(this IServiceCollection services)
    {
        services.AddScoped<
            Cluckwork.Application.Features.Eggs.IEggInventoryMovementRepository,
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
            Cluckwork.Application.Features.DailyEntries.LockDueDailyEntries.LockDueDailyEntriesHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Eggs.IEggOperationsModule,
            Cluckwork.Application.Features.Eggs.EggOperationsModule>();
        services.AddScoped<IEggGradeLookup, EggGradeLookup>();
        services.AddScoped<IEggStock, EggStock>();
        services.AddScoped<IDailyEntryLookup, DailyEntryLookup>();

        return services;
    }
}
