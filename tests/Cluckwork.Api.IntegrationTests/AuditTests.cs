namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #93 — the audit trail is domain data: written in the same transaction as
// the change (a failed action leaves nothing), actor captured from the JWT,
// viewer admin-only.
[Collection(IntegrationCollection.Name)]
public sealed class AuditTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record EntryDto(Guid Id, int Version);
    private sealed record AuditRow(
        Guid Id, DateTimeOffset OccurredAtUtc, string ActorEmail,
        string Action, string EntityType, Guid EntityId,
        string? Reason, string? DetailsJson);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private async Task<(HttpClient Client, string Email, Guid AccountId, Guid FarmId, Guid FlockId, Guid GradeId)>
        SetupAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, email, accountId, farmId, flockId, grades["Large"]);
    }

    [Fact]
    public async Task Adjust_WritesEvent_WithActorAndReason_FailedAdjustWritesNothing()
    {
        var (client, email, _, farmId, flockId, gradeId) = await SetupAsync();

        var record = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId = Guid.NewGuid(), flockId, date = Today,
            totalEggs = 100, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 0,
            grades = new[] { new { eggGradeId = gradeId, quantity = 90 } }
        });
        var entryId = (await record.Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        var version = (await client.GetFromJsonAsync<EntryDto>($"/api/v1/daily-entries/{entryId}"))!.Version;

        // A FAILED adjust (stale version → 409) must leave no event.
        var failed = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/adjust", Guid.NewGuid().ToString(), new
            {
                version = version + 7, totalEggs = 90, crackedEggs = 0, dirtyEggs = 0,
                discardedEggs = 0, mortalityCount = 0, reason = "stale"
            });
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);

        var afterFailure = await client.GetFromJsonAsync<List<AuditRow>>(
            $"/api/v1/audit?entityId={entryId}");
        Assert.Empty(afterFailure!);

        // The successful adjust writes exactly one, with actor + reason.
        var ok = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/adjust", Guid.NewGuid().ToString(), new
            {
                version, totalEggs = 90, crackedEggs = 0, dirtyEggs = 0,
                discardedEggs = 0, mortalityCount = 0, reason = "recount at pickup"
            });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var rows = await client.GetFromJsonAsync<List<AuditRow>>($"/api/v1/audit?entityId={entryId}");
        var row = Assert.Single(rows!);
        Assert.Equal("DailyEntry.Adjust", row.Action);
        Assert.Equal("DailyEntry", row.EntityType);
        Assert.Equal(email, row.ActorEmail);
        Assert.Equal("recount at pickup", row.Reason);
    }

    [Fact]
    public async Task CriticalActions_LandInTheTrail_FilteredByAction()
    {
        var (client, _, _, farmId, flockId, _) = await SetupAsync();

        // Flock edit + cull + deplete → three distinct actions.
        var flockGet = await client.GetAsync($"/api/v1/flocks/{flockId}");
        Assert.Equal(HttpStatusCode.OK, flockGet.StatusCode);
        var putBody = new
        {
            name = "Audited flock", breed = "Test Breed",
            placementDate = Today.AddDays(-30), initialCount = 100
        };
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/flocks/{flockId}")
        { Content = JsonContent.Create(putBody) };
        put.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(put)).StatusCode);

        await client.PostWithKeyAsync($"/api/v1/flocks/{flockId}/movements", Guid.NewGuid().ToString(),
            new { type = "Cull", quantity = 3, date = Today, note = "culled sick birds" });
        await client.PostWithKeyAsync($"/api/v1/flocks/{flockId}/deplete", Guid.NewGuid().ToString());

        var updates = await client.GetFromJsonAsync<List<AuditRow>>("/api/v1/audit?action=Flock.Update");
        Assert.Contains(updates!, r => r.EntityId == flockId);

        var movements = await client.GetFromJsonAsync<List<AuditRow>>("/api/v1/audit?action=Flock.BirdMovement");
        var cull = Assert.Single(movements!, r => r.EntityId == flockId);
        Assert.Equal("culled sick birds", cull.Reason);
        Assert.Contains("Cull", cull.DetailsJson);

        var depletions = await client.GetFromJsonAsync<List<AuditRow>>("/api/v1/audit?action=Flock.Deplete");
        Assert.Contains(depletions!, r => r.EntityId == flockId);
    }

    // Cross-tenant isolation: the trail is scoped by the same global filter as
    // every entity, and a foreign entityId is indistinguishable from a
    // nonexistent one (no existence oracle).
    [Fact]
    public async Task Viewer_NeverCrossesTenants()
    {
        var (clientA, _, _, farmA, flockA, gradeA) = await SetupAsync();

        // Tenant A produces an audited action.
        var record = await clientA.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId = farmA, houseId = Guid.NewGuid(), flockId = flockA, date = Today,
            totalEggs = 50, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 0,
            grades = new[] { new { eggGradeId = gradeA, quantity = 40 } }
        });
        var entryId = (await record.Content.ReadFromJsonAsync<Created>())!.Id;
        await clientA.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        var version = (await clientA.GetFromJsonAsync<EntryDto>($"/api/v1/daily-entries/{entryId}"))!.Version;
        await clientA.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/adjust", Guid.NewGuid().ToString(), new
        {
            version, totalEggs = 45, crackedEggs = 0, dirtyEggs = 0,
            discardedEggs = 0, mortalityCount = 0, reason = "tenant A only"
        });
        Assert.Single((await clientA.GetFromJsonAsync<List<AuditRow>>($"/api/v1/audit?entityId={entryId}"))!);

        // Tenant B's admin sees nothing — not by list, not by A's entity id.
        var (clientB, _, _, _, _, _) = await SetupAsync();
        Assert.Empty((await clientB.GetFromJsonAsync<List<AuditRow>>("/api/v1/audit"))!);
        Assert.Empty((await clientB.GetFromJsonAsync<List<AuditRow>>($"/api/v1/audit?entityId={entryId}"))!);
    }

    // Paging over the append-only trail: limit/offset windows are disjoint and
    // their concatenation is exactly the full newest-first list (Id tiebreak
    // keeps same-instant rows stable).
    [Fact]
    public async Task Paging_WindowsAreDisjoint_AndStitchBackNewestFirst()
    {
        var (client, _, _, _, flockId, _) = await SetupAsync();
        for (var i = 0; i < 3; i++)
            await client.PostWithKeyAsync($"/api/v1/flocks/{flockId}/movements", Guid.NewGuid().ToString(),
                new { type = "Cull", quantity = 1, date = Today, note = $"cull {i}" });

        var all = await client.GetFromJsonAsync<List<AuditRow>>(
            "/api/v1/audit?action=Flock.BirdMovement");
        Assert.Equal(3, all!.Count);
        Assert.True(all.Zip(all.Skip(1)).All(p => p.First.OccurredAtUtc >= p.Second.OccurredAtUtc));

        var page1 = await client.GetFromJsonAsync<List<AuditRow>>(
            "/api/v1/audit?action=Flock.BirdMovement&limit=2&offset=0");
        var page2 = await client.GetFromJsonAsync<List<AuditRow>>(
            "/api/v1/audit?action=Flock.BirdMovement&limit=2&offset=2");
        Assert.Equal(2, page1!.Count);
        Assert.Single(page2!);
        Assert.Equal(all.Select(r => r.Id), page1.Concat(page2!).Select(r => r.Id));
    }

    // Date filters are inclusive calendar days; to=9999-12-31 must not 500
    // (AddDays overflow guard from the codex round).
    [Fact]
    public async Task DateRange_IsInclusive_AndMaxDateIsSafe()
    {
        var (client, _, _, _, flockId, _) = await SetupAsync();
        await client.PostWithKeyAsync($"/api/v1/flocks/{flockId}/movements", Guid.NewGuid().ToString(),
            new { type = "Cull", quantity = 1, date = Today, note = "dated" });

        // Anchor on the row's actual UTC day so a midnight rollover can't flake.
        var row = Assert.Single((await client.GetFromJsonAsync<List<AuditRow>>(
            "/api/v1/audit?action=Flock.BirdMovement"))!);
        var day = DateOnly.FromDateTime(row.OccurredAtUtc.UtcDateTime);

        Assert.Single((await client.GetFromJsonAsync<List<AuditRow>>(
            $"/api/v1/audit?action=Flock.BirdMovement&from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}"))!);
        Assert.Empty((await client.GetFromJsonAsync<List<AuditRow>>(
            $"/api/v1/audit?action=Flock.BirdMovement&from={day.AddDays(1):yyyy-MM-dd}"))!);
        Assert.Empty((await client.GetFromJsonAsync<List<AuditRow>>(
            $"/api/v1/audit?action=Flock.BirdMovement&to={day.AddDays(-1):yyyy-MM-dd}"))!);
        Assert.Single((await client.GetFromJsonAsync<List<AuditRow>>(
            $"/api/v1/audit?action=Flock.BirdMovement&to=9999-12-31"))!);
    }

    // A MID-transaction domain failure (order void refused because payments
    // exist — the guard runs inside the transaction) must leave no audit row;
    // the eventual success writes exactly one. Also pins that Payment.Void
    // details carry the voided amount for the admin viewer.
    [Fact]
    public async Task MidTransactionFailure_WritesNothing_EventualVoidWritesOne()
    {
        var (client, _, _, farmId, flockId, gradeId) = await SetupAsync();

        var record = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId = Guid.NewGuid(), flockId, date = Today,
            totalEggs = 100, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 0,
            grades = new[] { new { eggGradeId = gradeId, quantity = 90 } }
        });
        var entryId = (await record.Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());

        var customer = await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Audit Buyer", phone = "555-0100" });
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { eggGradeId = gradeId, quantity = 10, unitPriceMinorUnits = 100 });
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/payments", Guid.NewGuid().ToString(),
            new { paymentDate = Today, amountMinorUnits = 500, method = "Cash" });

        // Void refused (payments exist) → the guard fails INSIDE the
        // transaction, after work has been done — nothing may persist.
        var refused = await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/void",
            Guid.NewGuid().ToString(), new { reason = "wrong order" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<AuditRow>>(
            $"/api/v1/audit?action=SalesOrder.Void&entityId={orderId}"))!);

        var payments = await client.GetFromJsonAsync<PaymentsPage>($"/api/v1/sales/{orderId}/payments");
        var payment = Assert.Single(payments!.Items);
        await client.PostWithKeyAsync($"/api/v1/payments/{payment.Id}/void", Guid.NewGuid().ToString(),
            new { version = payment.Version, reason = "clearing for order void" });

        var paymentVoid = Assert.Single((await client.GetFromJsonAsync<List<AuditRow>>(
            $"/api/v1/audit?action=Payment.Void&entityId={payment.Id}"))!);
        Assert.Contains("\"amountMinorUnits\":500", paymentVoid.DetailsJson);

        var allowed = await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/void",
            Guid.NewGuid().ToString(), new { reason = "wrong order" });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var orderVoid = Assert.Single((await client.GetFromJsonAsync<List<AuditRow>>(
            $"/api/v1/audit?action=SalesOrder.Void&entityId={orderId}"))!);
        Assert.Equal("wrong order", orderVoid.Reason);
    }

    private sealed record PaymentRow(Guid Id, int Version);
    private sealed record PaymentsPage(List<PaymentRow> Items);

    [Fact]
    public async Task Viewer_IsAdminOnly()
    {
        var (adminClient, _, accountId, _, _, _) = await SetupAsync();
        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));

        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/v1/audit")).StatusCode);
    }
}
