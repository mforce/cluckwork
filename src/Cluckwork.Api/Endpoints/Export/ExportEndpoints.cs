namespace Cluckwork.Api.Endpoints.Export;

using System.IO.Compression;
using System.Text.Json;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Export;
using Cluckwork.Infrastructure.Persistence;

// #95 — manual backup (spec §17.5). Read-only, admin-only (group policy in
// Program.cs): exports are a bulk copy of the account, money data included.
public static class ExportEndpoints
{
    private static readonly JsonSerializerOptions ManifestJson =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static RouteGroupBuilder MapExportEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/all", ExportAll)
            .WithName("ExportAccountBackup")
            .WithSummary("Download every dataset as CSVs in one zip, plus a manifest.");

        group.MapGet("/{dataset}", ExportDataset)
            .WithName("ExportDataset")
            .WithSummary("Download one dataset as CSV.");

        return group;
    }

    private static async Task<IResult> ExportDataset(
        string dataset, IExportQueries exports, IClock clock,
        TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var table = await exports.GetTableAsync(dataset, ct);
        if (table is null) return Results.NotFound();

        return Results.File(
            CsvExport.ToBytes(table),
            "text/csv; charset=utf-8",
            $"cluckwork-{dataset}-{clock.UtcNow:yyyyMMdd}.csv");
    }

    private static async Task<IResult> ExportAll(
        IExportQueries exports, IClock clock,
        TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var counts = new Dictionary<string, int>();
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in exports.Datasets)
            {
                var table = (await exports.GetTableAsync(name, ct))!;
                counts[name] = table.Rows.Count;
                var entry = zip.CreateEntry($"{name}.csv", CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await stream.WriteAsync(CsvExport.ToBytes(table), ct);
            }

            var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using var manifestStream = manifest.Open();
            await JsonSerializer.SerializeAsync(manifestStream,
                new { exportedAtUtc = clock.UtcNow, datasets = counts }, ManifestJson, ct);
        }

        return Results.File(
            buffer.ToArray(),
            "application/zip",
            $"cluckwork-backup-{clock.UtcNow:yyyyMMdd}.zip");
    }
}
