using Cluckwork.Application.Features.Insights;
using Cluckwork.Infrastructure.Insights;

namespace Cluckwork.Api.Hosting.Modules;

internal static class InsightsModuleServiceCollectionExtensions
{
    public static IServiceCollection AddInsightsModule(this IServiceCollection services)
    {
        services.AddScoped<
            Cluckwork.Application.Features.Audit.IAuditEventRepository,
            AuditEventRepository>();
        services.AddScoped<
            Cluckwork.Application.Features.Export.IExportQueries,
            ExportQueries>();
        services.AddScoped<
            Cluckwork.Application.Features.Reports.IReportQueries,
            ReportQueries>();
        services.AddScoped<IInsightsModule, InsightsModule>();

        return services;
    }
}
