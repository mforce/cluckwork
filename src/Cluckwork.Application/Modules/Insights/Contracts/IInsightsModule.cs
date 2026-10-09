using Cluckwork.Application.Modules.Insights.Export;
using Cluckwork.Application.Modules.Insights.Reports;

namespace Cluckwork.Application.Modules.Insights.Contracts;

public interface IInsightsModule : IReportQueries, IExportQueries
{
    // #800 — connectedAppsOnly keeps actions taken through any connected app;
    // connectedAppClientId keeps one app's, and implies the first.
    Task<IReadOnlyList<AuditEventRead>> ListAuditEventsAsync(
        string? action, string? entityType, Guid? entityId, DateOnly? from, DateOnly? to,
        bool connectedAppsOnly, string? connectedAppClientId,
        int limit, int offset, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, EntityProvenance>> GetProvenanceAsync(
        string entityType, IReadOnlyCollection<Guid> entityIds, CancellationToken ct = default);
}
