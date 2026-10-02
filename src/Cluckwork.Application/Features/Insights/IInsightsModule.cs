using Cluckwork.Application.Features.Audit;
using Cluckwork.Application.Features.Export;
using Cluckwork.Application.Features.Reports;

namespace Cluckwork.Application.Features.Insights;

public interface IInsightsModule : IReportQueries, IExportQueries
{
    Task<IReadOnlyList<AuditEventRead>> ListAuditEventsAsync(
        string? action, string? entityType, Guid? entityId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, EntityProvenance>> GetProvenanceAsync(
        string entityType, IReadOnlyCollection<Guid> entityIds, CancellationToken ct = default);
}
