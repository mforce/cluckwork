using Cluckwork.Application.Features.Audit;
using Cluckwork.Application.Features.Export;
using Cluckwork.Application.Features.Insights;
using Cluckwork.Application.Features.Reports;

namespace Cluckwork.Infrastructure.Insights;

public sealed class InsightsModule(
    IReportQueries reports, IExportQueries exports, IAuditEventRepository audit) : IInsightsModule
{
    public Task<ProductionReport> GetProductionAsync(
        DateOnly from, DateOnly to, Guid? flockId = null, CancellationToken ct = default) =>
        reports.GetProductionAsync(from, to, flockId, ct);

    public Task<SalesSummary> GetSalesAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        reports.GetSalesAsync(from, to, ct);

    public Task<ExpenseSummary> GetExpensesAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        reports.GetExpensesAsync(from, to, ct);

    public Task<ProfitReport> GetProfitAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        reports.GetProfitAsync(from, to, ct);

    public IReadOnlyList<string> Datasets => exports.Datasets;

    public ExportDataset? GetDataset(string dataset) => exports.GetDataset(dataset);

    public Task<IAsyncDisposable> BeginConsistentReadAsync(CancellationToken ct = default) =>
        exports.BeginConsistentReadAsync(ct);

    public Task<IReadOnlyList<AuditEventRead>> ListAuditEventsAsync(
        string? action, string? entityType, Guid? entityId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default) =>
        audit.ListAsync(action, entityType, entityId, from, to, limit, offset, ct);

    public Task<IReadOnlyDictionary<Guid, EntityProvenance>> GetProvenanceAsync(
        string entityType, IReadOnlyCollection<Guid> entityIds, CancellationToken ct = default) =>
        audit.GetProvenanceAsync(entityType, entityIds, ct);
}
