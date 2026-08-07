namespace Cluckwork.Api.Endpoints.Export;

using System.IO.Compression;
using System.IO.Pipelines;
using System.Text.Json;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Export;
using Cluckwork.Infrastructure.Persistence;

// #95 — manual backup (spec §17.5). Read-only, admin-only (group policy in
// Program.cs): exports are a bulk copy of the account, money data included.
// Responses stream — rows go database → CSV encoder → wire without ever
// materializing an export in memory (codex review of #96).
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
        string dataset, IExportQueries exports, IAuditWriter audit,
        IUnitOfWork unitOfWork, IClock clock, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var table = exports.GetDataset(dataset);
        if (table is null) return Results.NotFound();

        // Exports are auditable actions (spec §18) — and the exfiltration
        // trail must commit BEFORE the first byte streams, so an aborted
        // download still leaves its trace.
        await audit.WriteAsync(AuditActions.AccountExport, "Account", tenant.AccountId,
            details: new { dataset }, ct: ct);
        await unitOfWork.SaveChangesAsync(ct);

        return Results.Stream(
            output => CsvExport.WriteAsync(table, output, ct),
            "text/csv; charset=utf-8",
            $"cluckwork-{dataset}-{clock.UtcNow:yyyyMMdd}.csv");
    }

    private static async Task<IResult> ExportAll(
        IExportQueries exports, IAuditWriter audit,
        IUnitOfWork unitOfWork, IClock clock, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        await audit.WriteAsync(AuditActions.AccountExport, "Account", tenant.AccountId,
            details: new { scope = "all" }, ct: ct);
        await unitOfWork.SaveChangesAsync(ct);

        return Results.Stream(async output =>
        {
            // The zip is built into an in-memory Pipe, not the response:
            // ZipArchive still closes entries with synchronous writes even via
            // its .NET 10 async APIs (WrappedStream lacks a DisposeAsync
            // override), and Kestrel forbids sync IO on the response body.
            // Sync writes into the pipe are memory writes; the reader side
            // copies to the wire async, with memory bounded by the pause
            // threshold (1 MiB) — still a stream, never a full buffer.
            var pipe = new Pipe(new PipeOptions(
                pauseWriterThreshold: 1 << 20, resumeWriterThreshold: 1 << 19));
            var producer = Task.Run(() => WriteZipAsync(pipe.Writer, exports, clock, ct), ct);
            try
            {
                await pipe.Reader.CopyToAsync(output, ct);
            }
            finally
            {
                // Unblocks a producer waiting on backpressure if the client
                // aborted mid-download.
                await pipe.Reader.CompleteAsync();
            }
            await producer;
        },
        "application/zip",
        $"cluckwork-backup-{clock.UtcNow:yyyyMMdd}.zip");
    }

    private static async Task WriteZipAsync(
        PipeWriter writer, IExportQueries exports, IClock clock, CancellationToken ct)
    {
        try
        {
            // One repeatable-read snapshot for the whole zip: every CSV sees
            // the same instant, so a write racing the backup can't leave
            // child rows whose parents are missing.
            await using var _ = await exports.BeginConsistentReadAsync(ct);

            var counts = new Dictionary<string, int>();
            var zipStream = writer.AsStream(leaveOpen: true);
            var zip = await ZipArchive.CreateAsync(zipStream, ZipArchiveMode.Create,
                leaveOpen: true, entryNameEncoding: null, cancellationToken: ct);
            await using (zip.ConfigureAwait(false))
            {
                foreach (var name in exports.Datasets)
                {
                    var entry = zip.CreateEntry($"{name}.csv", CompressionLevel.Optimal);
                    await using var stream = await entry.OpenAsync(ct);
                    counts[name] = await CsvExport.WriteAsync(exports.GetDataset(name)!, stream, ct);
                }

                var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var manifestStream = await manifest.OpenAsync(ct);
                await JsonSerializer.SerializeAsync(manifestStream,
                    new { exportedAtUtc = clock.UtcNow, datasets = counts }, ManifestJson, ct);
            }

            await writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            // Surfaces on the reader side as a failed copy; never silently
            // truncates the zip.
            await writer.CompleteAsync(ex);
        }
    }
}
