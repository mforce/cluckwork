namespace Cluckwork.Application.Features.Export;

// #95 — manual backup. One flat dataset per name; rows stream from the
// database so an export never materializes in memory, and cells stay typed so
// the CSV layer can format invariantly and apply the formula guard to strings
// only.
[ModuleContract("Insights")]
public sealed record ExportDataset(string[] Header, IAsyncEnumerable<object?[]> Rows);
