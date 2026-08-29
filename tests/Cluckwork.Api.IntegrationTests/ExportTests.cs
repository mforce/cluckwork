namespace Cluckwork.Api.IntegrationTests;

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #95 — manual backup: admin-only CSV export per dataset + full-account zip.
// The CSVs must be tenant-scoped, RFC 4180-escaped, and formula-guarded.
[Collection(IntegrationCollection.Name)]
public sealed class ExportTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);

    private static readonly string[] AllDatasets =
    [
        "flocks", "bird-movements", "daily-entries", "daily-entry-grades",
        "egg-grades", "egg-lots", "customers", "sales-orders",
        "sales-order-items", "sales-order-allocations", "payments",
        "inventory-items", "inventory-lots", "inventory-movements",
        "feed-usages", "water-usages", "expense-categories", "expenses",
        "egg-inventory-movements", "audit-events",
    ];

    private async Task<(HttpClient Client, Guid AccountId, Guid FarmId, Guid FlockId)> SetupAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, accountId, farmId, flockId);
    }

    // #396 — an export is the farm's own copy of its records, so it must stay
    // self-describing. Without these two columns it says a day had N cracked
    // eggs but not whether they became stock or a loss, and that is NOT
    // recoverable from the grade catalog afterwards — the snapshot exists
    // precisely because the catalog can change. dailyEntryKind is the other
    // half: the only field naming which counter a grade serves, and the only
    // one that survives a rename.
    [Fact]
    public async Task Export_CarriesTheConditionSnapshotsAndTheGradeBinding()
    {
        var (client, accountId, farmId, _) = await SetupAsync();

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.EggGrades.Add(Domain.Eggs.EggGrade.Create(
                Guid.NewGuid(), accountId, farmId, "Cracked",
                Domain.Eggs.EggGradeType.Quality, 50, isSaleable: true,
                dailyEntryKind: Domain.Eggs.DailyEntryKind.Cracked));
            await db.SaveChangesAsync();
        });

        var gradesCsv = await (await client.GetAsync("/api/v1/export/egg-grades"))
            .Content.ReadAsStringAsync();
        var entriesHeader = (await (await client.GetAsync("/api/v1/export/daily-entries"))
            .Content.ReadAsStringAsync()).Split('\n')[0];

        // Header, then the value — a header-only check passes against a column
        // wired to the wrong property.
        Assert.Contains("dailyEntryKind", gradesCsv.Split('\n')[0], StringComparison.Ordinal);
        Assert.Contains("Cracked", gradesCsv, StringComparison.Ordinal);

        Assert.Contains("crackedGradeId", entriesHeader, StringComparison.Ordinal);
        Assert.Contains("dirtyGradeId", entriesHeader, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_IsAdminOnly_UnknownDatasetIs404()
    {
        var (admin, accountId, _, _) = await SetupAsync();
        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));

        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync("/api/v1/export/flocks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync("/api/v1/export/all")).StatusCode);

        var csv = await admin.GetAsync("/api/v1/export/flocks");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/export/no-such-dataset")).StatusCode);
    }

    // #613 — these no-FlockId child datasets rely on the export policy rather
    // than a flock query filter. Pin each concrete path so a future literal
    // route or endpoint-level policy override cannot hide behind /flocks.
    [Theory]
    [InlineData("daily-entry-grades")]
    [InlineData("egg-inventory-movements")]
    public async Task FlockDerivedChildExport_IsAdminOnly(string dataset)
    {
        var (_, accountId, _, _) = await SetupAsync();
        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));

        Assert.Equal(HttpStatusCode.Forbidden,
            (await worker.GetAsync($"/api/v1/export/{dataset}")).StatusCode);
    }

    [Fact]
    public async Task Csv_EscapesRfc4180_AndGuardsFormulas()
    {
        var (client, _, _, _) = await SetupAsync();

        // Name starts like a formula; note carries a comma, a quote, and a
        // newline — the classic CSV breakage kit.
        await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(), new
        {
            name = "=SUM(A1:A9)",
            phone = "555-0100",
            note = "line one\nsays \"hi\", twice",
        });

        var res = await client.GetAsync("/api/v1/export/customers");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var bytes = await res.Content.ReadAsByteArrayAsync();

        // UTF-8 BOM for Excel.
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3));

        var text = Encoding.UTF8.GetString(bytes.AsSpan(3));
        var lines = text.Split("\r\n");
        Assert.Equal("id,name,phone,email,address,note", lines[0]);
        // Formula guard: leading apostrophe forces text rendering.
        Assert.Contains("'=SUM(A1:A9)", text);
        Assert.DoesNotContain(",=SUM", text);
        // RFC 4180: the messy note is quoted, inner quotes doubled, and the
        // embedded newline stays INSIDE the quoted cell.
        Assert.Contains("\"line one\nsays \"\"hi\"\", twice\"", text);
    }

    [Fact]
    public async Task FullBackup_ContainsEveryDatasetAndManifestCounts()
    {
        var (client, _, farmId, flockId) = await SetupAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        await client.PostWithKeyAsync($"/api/v1/flocks/{flockId}/movements", Guid.NewGuid().ToString(),
            new { type = "Cull", quantity = 1, date = today, note = "for the manifest" });
        _ = farmId;

        var res = await client.GetAsync("/api/v1/export/all");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/zip", res.Content.Headers.ContentType!.MediaType);

        using var zip = new ZipArchive(await res.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        foreach (var dataset in AllDatasets)
            Assert.NotNull(zip.GetEntry($"{dataset}.csv"));
        Assert.Equal(AllDatasets.Length + 1, zip.Entries.Count); // + manifest

        using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        using var manifest = await JsonDocument.ParseAsync(manifestStream);
        var datasets = manifest.RootElement.GetProperty("datasets");
        Assert.Equal(1, datasets.GetProperty("flocks").GetInt32());
        Assert.Equal(1, datasets.GetProperty("bird-movements").GetInt32());
        Assert.Equal(0, datasets.GetProperty("payments").GetInt32());
        Assert.True(manifest.RootElement.TryGetProperty("exportedAtUtc", out _));

        // The movement audit event (Flock.BirdMovement) rides along too.
        Assert.True(datasets.GetProperty("audit-events").GetInt32() >= 1);
    }

    // Spec §18: export is an auditable action — the trail must show WHO bulk-
    // copied the account, and the event lands before the stream starts.
    [Fact]
    public async Task Export_WritesAuditTrail()
    {
        var (client, _, _, _) = await SetupAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/export/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/export/all")).StatusCode);

        var rows = await client.GetFromJsonAsync<List<AuditRow>>("/api/v1/audit?action=Account.Export");
        Assert.Equal(2, rows!.Count);
        Assert.Contains(rows, r => r.DetailsJson!.Contains("\"dataset\":\"customers\""));
        Assert.Contains(rows, r => r.DetailsJson!.Contains("\"scope\":\"all\""));

        // A failed export (unknown dataset → 404) leaves no event.
        await client.GetAsync("/api/v1/export/no-such-dataset");
        Assert.Equal(2, (await client.GetFromJsonAsync<List<AuditRow>>("/api/v1/audit?action=Account.Export"))!.Count);
    }

    private sealed record AuditRow(Guid Id, string Action, string? DetailsJson);

    [Fact]
    public async Task Export_NeverCrossesTenants()
    {
        var (clientA, _, _, _) = await SetupAsync();
        // Tenant A's flock exists; tenant B's flocks export must hold only
        // B's own single seeded flock — never A's rows.
        var flocksA = await (await clientA.GetAsync("/api/v1/export/flocks")).Content.ReadAsStringAsync();
        var idA = flocksA.Split("\r\n")[1].Split(',')[0];

        var (clientB, _, _, _) = await SetupAsync();
        var flocksB = await (await clientB.GetAsync("/api/v1/export/flocks")).Content.ReadAsStringAsync();

        Assert.Equal(3, flocksB.Split("\r\n").Length); // header + 1 row + trailing CRLF
        Assert.DoesNotContain(idA, flocksB);
    }
}
