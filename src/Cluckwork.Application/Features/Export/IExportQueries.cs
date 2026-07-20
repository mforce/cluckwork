namespace Cluckwork.Application.Features.Export;

// #95 — manual backup. One flat dataset per name; rows stream from the
// database so an export never materializes in memory, and cells stay typed so
// the CSV layer can format invariantly and apply the formula guard to strings
// only.
public sealed record ExportDataset(string[] Header, IAsyncEnumerable<object?[]> Rows);

public interface IExportQueries
{
    /// <summary>Every exportable dataset name, in the order the full backup packs them.</summary>
    IReadOnlyList<string> Datasets { get; }

    /// <summary>
    /// The named dataset as a deferred row stream (query runs on enumeration),
    /// or null when no such dataset exists.
    /// </summary>
    ExportDataset? GetDataset(string dataset);

    /// <summary>
    /// Opens a single read snapshot for everything enumerated until disposal —
    /// the full backup uses it so no dataset can see child rows whose parents
    /// were exported before the parents existed.
    /// </summary>
    Task<IAsyncDisposable> BeginConsistentReadAsync(CancellationToken ct = default);
}
