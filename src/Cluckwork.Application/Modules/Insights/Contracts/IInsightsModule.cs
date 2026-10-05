using Cluckwork.Application.Modules.Insights.Export;
using Cluckwork.Application.Modules.Insights.Reports;

namespace Cluckwork.Application.Modules.Insights.Contracts;

public interface IInsightsModule : IReportQueries, IExportQueries
{
    Task<IReadOnlyList<AuditEventRead>> ListAuditEventsAsync(
        string? action, string? entityType, Guid? entityId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, EntityProvenance>> GetProvenanceAsync(
        string entityType, IReadOnlyCollection<Guid> entityIds, CancellationToken ct = default);
}
