using Cluckwork.Application.Modules.Insights.Contracts;
using Cluckwork.Infrastructure.Modules.Insights.Repositories;

namespace Cluckwork.Api.Modules.Insights;

internal static class InsightsModuleServiceCollectionExtensions
{
    public static IServiceCollection AddInsightsModule(this IServiceCollection services)
    {
        services.AddScoped<
            Cluckwork.Application.Modules.Insights.Audit.IAuditEventRepository,
            AuditEventRepository>();
        services.AddScoped<
            Cluckwork.Application.Modules.Insights.Export.IExportQueries,
            ExportQueries>();
        services.AddScoped<
            Cluckwork.Application.Modules.Insights.Reports.IReportQueries,
            ReportQueries>();
        services.AddScoped<IInsightsModule, InsightsModule>();

        return services;
    }
}
