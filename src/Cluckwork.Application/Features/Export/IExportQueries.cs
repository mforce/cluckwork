namespace Cluckwork.Application.Features.Export;

// #95 — manual backup. One flat table per dataset; cells stay typed so the
// CSV layer can format invariantly and apply the formula guard to strings only.
public sealed record ExportTable(string[] Header, IReadOnlyList<object?[]> Rows);

public interface IExportQueries
{
    /// <summary>Every exportable dataset name, in the order the full backup packs them.</summary>
    IReadOnlyList<string> Datasets { get; }

    /// <summary>The named dataset flattened for CSV, or null when no such dataset exists.</summary>
    Task<ExportTable?> GetTableAsync(string dataset, CancellationToken ct = default);
}
