namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;

// #103 — the role → capability matrix (spec §5.1/§5.3) and worker flock
// scoping. The SPA hides controls; these tests prove the API refuses anyway.
[Collection(IntegrationCollection.Name)]
public sealed class RoleMatrixTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record AuditRow(Guid Id, string Action, string? DetailsJson);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private async Task<(Guid AccountId, Guid FarmId, Guid FlockId, Guid GradeId)> SeedFarmAsync()
    {
        var owner = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(owner);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        return (accountId, farmId, flockId, grades["Large"]);
    }

    private async Task<HttpClient> ClientAsync(Guid accountId, string? role)
    {
        var email = $"r-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, role);
        return factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
    }

    private static object EntryBody(Guid farmId, Guid flockId, Guid gradeId, int eggs = 50) => new
    {
        farmId, houseId = Guid.NewGuid(), flockId, date = Today,
        totalEggs = eggs, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
        mortalityCount = 0,
        grades = new[] { new { eggGradeId = gradeId, quantity = eggs } }
    };

    [Fact]
    public async Task Manager_HasCorrectiveTier_ButNoUserManagement()
    {
        var (accountId, farmId, flockId, gradeId) = await SeedFarmAsync();
        var manager = await ClientAsync(accountId, Roles.Manager);

        // Old AdminOnly gates now admit Managers: catalog config + money reads.
        Assert.Equal(HttpStatusCode.Created, (await manager.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = $"M-{Guid.NewGuid():N}"[..10], gradeType = "Custom", sortOrder = 99, isSaleable = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/v1/export/flocks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/v1/expense-categories")).StatusCode);

        // Production too (Manager ⊇ Worker).
        Assert.Equal(HttpStatusCode.Created, (await manager.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockId, gradeId))).StatusCode);

        // But user management is the Owner's alone.
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostWithKeyAsync(
            "/api/v1/users", Guid.NewGuid().ToString(),
            new { email = "x@test.local", password = "TestPassw0rd!23", role = "Worker" })).StatusCode);
    }

    // Owner seeds a confirmed order carrying one payment; returns the ids so
    // other roles can be tested against void.
    private async Task<(Guid OrderId, Guid PaymentId)> ConfirmedOrderWithPaymentAsync(
        Guid accountId, Guid farmId, Guid gradeId, HttpClient owner)
    {
        var productId = await factory.SeedProductAsync(accountId, farmId, gradeId, $"P-{Guid.NewGuid():N}"[..12], 100);
        await factory.SeedEggLotAsync(accountId, gradeId, 200);
        var customer = await owner.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = $"Buyer {Guid.NewGuid():N}"[..14], phone = "1" });
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await owner.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;
        await owner.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 10 });
        await owner.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        var pay = await owner.PostWithKeyAsync($"/api/v1/sales/{orderId}/payments",
            Guid.NewGuid().ToString(), new { paymentDate = Today, amountMinorUnits = 300, method = "Cash" });
        var paymentId = (await pay.Content.ReadFromJsonAsync<PaymentCreated>())!.Id;
        return (orderId, paymentId);
    }

    // The widening's whole point: Manager gets the corrective tier including
    // BOTH voids (a future narrowing to Owner-only would fail here).
    [Fact]
    public async Task Manager_CanVoidSaleAndPayment()
    {
        var (accountId, farmId, _, gradeId) = await SeedFarmAsync();
        var owner = await ClientAsync(accountId, Roles.Owner);
        var manager = await ClientAsync(accountId, Roles.Manager);
        var (orderId, paymentId) = await ConfirmedOrderWithPaymentAsync(accountId, farmId, gradeId, owner);

        var payment = (await owner.GetFromJsonAsync<PaymentsPage>($"/api/v1/sales/{orderId}/payments"))!
            .Items.Single(p => p.Id == paymentId);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostWithKeyAsync(
            $"/api/v1/payments/{paymentId}/void", Guid.NewGuid().ToString(),
            new { version = payment.Version, reason = "manager void" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/void", Guid.NewGuid().ToString(),
            new { reason = "manager void" })).StatusCode);
    }

    // A plain Worker is refused across the entire corrective + money-config
    // surface — the "missing gate" regression class this repo has shipped
    // before (#104 panel).
    [Fact]
    public async Task Worker_DeniedFromCorrectiveAndMoneyConfig()
    {
        var (accountId, farmId, _, gradeId) = await SeedFarmAsync();
        var owner = await ClientAsync(accountId, Roles.Owner);
        var worker = await ClientAsync(accountId, (string?)null);
        var (orderId, paymentId) = await ConfirmedOrderWithPaymentAsync(accountId, farmId, gradeId, owner);

        foreach (var probe in new (string Method, string Path)[]
        {
            ("GET", "/api/v1/users"), ("GET", "/api/v1/audit"),
            ("GET", "/api/v1/export/flocks"), ("GET", "/api/v1/expense-categories"),
            ("GET", "/api/v1/reports/sales"),
        })
            Assert.Equal(HttpStatusCode.Forbidden,
                (await worker.GetAsync(probe.Path)).StatusCode);

        // ...but the sell-flow reads stay OPEN: workers build orders, so the
        // customer directory + order book are SalesFlow, not SalesAccess (#127).
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync("/api/v1/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync("/api/v1/sales")).StatusCode);

        foreach (var (path, body) in new (string, object)[]
        {
            ($"/api/v1/sales/{orderId}/void", new { reason = "no" }),
            ($"/api/v1/payments/{paymentId}/void", new { version = 0, reason = "no" }),
            ("/api/v1/egg-grades", new { name = "Nope", gradeType = "Custom", sortOrder = 1, isSaleable = false }),
            ("/api/v1/expenses", new { expenseCategoryId = Guid.NewGuid(), date = Today, description = "x", amountMinorUnits = 1 }),
        })
            Assert.Equal(HttpStatusCode.Forbidden,
                (await worker.PostWithKeyAsync(path, Guid.NewGuid().ToString(), body)).StatusCode);
    }

    [Fact]
    public async Task Sales_SellsAndSettles_ButNeverTouchesProductionOrExpenses()
    {
        var (accountId, farmId, flockId, gradeId) = await SeedFarmAsync();
        var productId = await factory.SeedProductAsync(accountId, farmId, gradeId, "Large Eggs", 100);
        var sales = await ClientAsync(accountId, Roles.Sales);

        // Customers + orders + payments: allowed.
        var customer = await sales.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Sales Customer", phone = "555-0100" });
        Assert.Equal(HttpStatusCode.Created, customer.StatusCode);
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await sales.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        Assert.Equal(HttpStatusCode.Created, order.StatusCode);
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.Created, (await sales.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sales.GetAsync(
            $"/api/v1/sales/{orderId}/payments")).StatusCode);

        // The customer directory + order book reads (#127) — SalesAccess ⊂ SalesFlow.
        Assert.Equal(HttpStatusCode.OK, (await sales.GetAsync("/api/v1/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sales.GetAsync("/api/v1/sales")).StatusCode);

        // Production capture and money config: refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockId, gradeId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/v1/expense-categories")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/v1/users")).StatusCode);

        // Voiding a SALE is corrective too — not just voiding a payment.
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/void", Guid.NewGuid().ToString(),
            new { reason = "no" })).StatusCode);
    }

    [Fact]
    public async Task ReadOnly_SeesViews_ChangesNothing()
    {
        var (accountId, farmId, flockId, gradeId) = await SeedFarmAsync();
        var reader = await ClientAsync(accountId, Roles.ReadOnly);

        // Views: stock, lots, production report, history-style reads.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/stock")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/stock/lots")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/daily-entries")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(
            "/api/v1/reports/production")).StatusCode);

        // Writes anywhere: refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockId, gradeId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Nope", phone = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId = Guid.NewGuid(), orderDate = Today })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/expense-categories")).StatusCode);
        // Customer directory + order book reads (PII + order financials) deny
        // ReadOnly (#127): these were ungated and leaked to every tier.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/v1/customers/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/sales")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/v1/sales/{Guid.NewGuid()}")).StatusCode);
        // Money routes (SalesAccess) deny ReadOnly — reads included.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/customers/balances")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostWithKeyAsync(
            $"/api/v1/sales/{Guid.NewGuid()}/payments", Guid.NewGuid().ToString(),
            new { paymentDate = Today, amountMinorUnits = 1, method = "Cash" })).StatusCode);
    }

    [Fact]
    public async Task Worker_FlockScoping_FirstAssignmentNarrows_RemovalRestores()
    {
        var (accountId, farmId, flockA, gradeId) = await SeedFarmAsync();
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, (string?)null);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));
        var ownerEmail = $"o2-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, ownerEmail, Roles.Owner);
        var owner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(ownerEmail));

        var workerId = (await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users"))!
            .Single(u => u.Email == workerEmail).Id;

        // Unscoped: any flock records.
        Assert.Equal(HttpStatusCode.Created, (await worker.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockB, gradeId))).StatusCode);

        // Owner assigns flock A → audit row; duplicate 409; worker narrowed.
        var assign = await owner.PostWithKeyAsync(
            $"/api/v1/users/{workerId}/flock-assignments", Guid.NewGuid().ToString(),
            new { flockId = flockA });
        Assert.Equal(HttpStatusCode.Created, assign.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostWithKeyAsync(
            $"/api/v1/users/{workerId}/flock-assignments", Guid.NewGuid().ToString(),
            new { flockId = flockA })).StatusCode);
        var audits = await owner.GetFromJsonAsync<List<AuditRow>>(
            $"/api/v1/audit?action=User.FlockAssign&entityId={workerId}");
        Assert.Single(audits!);

        Assert.Equal(HttpStatusCode.Created, (await worker.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockA, gradeId))).StatusCode);
        var refused = await worker.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockB, gradeId, eggs: 40));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("FlockScope.NotAssigned", await refused.Content.ReadAsStringAsync());

        // Water + feed record paths share the guard.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await worker.PostWithKeyAsync(
            "/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId = flockB, date = Today, quantity = 10.5, unit = "L", source = "Municipal" })).StatusCode);

        // Elevated roles are never scoped.
        Assert.Equal(HttpStatusCode.Created, (await owner.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockB, gradeId, eggs: 30))).StatusCode);

        // Removing the assignment restores account-wide access.
        var assignments = await owner.GetFromJsonAsync<List<AssignmentRow>>(
            $"/api/v1/users/{workerId}/flock-assignments");
        var assignmentId = Assert.Single(assignments!).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(
            WithKey(new HttpRequestMessage(HttpMethod.Delete,
                $"/api/v1/users/{workerId}/flock-assignments/{assignmentId}")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await worker.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockB, gradeId, eggs: 20))).StatusCode);
    }

    // Multi-role principals resolve by precedence (highest wins) and unknown
    // roles are denied outright, never treated as workers (#104 panel).
    [Fact]
    public async Task MultiRole_UsesPrecedence_UnknownRole_IsDenied()
    {
        var (accountId, farmId, flockId, gradeId) = await SeedFarmAsync();

        // Owner+ReadOnly = Owner: the sales flow must NOT be vetoed.
        var email = $"or-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, Roles.Owner);
        await factory.AddRoleAsync(email, Roles.ReadOnly);
        var ownerReadOnly = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        Assert.Equal(HttpStatusCode.Created, (await ownerReadOnly.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Precedence Buyer", phone = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ownerReadOnly.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockId, gradeId))).StatusCode);

        // Sales+ReadOnly = Sales: still no production, but the sales flow WORKS
        // (a lowest-wins reversal would let ReadOnly veto it — this asserts the
        // success side the precedence fix guarantees).
        var srEmail = $"sr-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, srEmail, Roles.Sales);
        await factory.AddRoleAsync(srEmail, Roles.ReadOnly);
        var salesReadOnly = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(srEmail));
        Assert.Equal(HttpStatusCode.Forbidden, (await salesReadOnly.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockId, gradeId, eggs: 30))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await salesReadOnly.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "SR Buyer", phone = "1" })).StatusCode);

        // Manager+Sales = Manager: production allowed (the exact pair the
        // policy comment warns lowest-wins would break).
        var msEmail = $"ms-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, msEmail, Roles.Manager);
        await factory.AddRoleAsync(msEmail, Roles.Sales);
        var managerSales = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(msEmail));
        Assert.Equal(HttpStatusCode.Created, (await managerSales.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockId, gradeId, eggs: 25))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await managerSales.GetAsync("/api/v1/audit")).StatusCode);

        // A user carrying ONLY an unrecognized role: denied everywhere — writes
        // AND the open production READS (the DefaultPolicy excludes it, closing
        // codex's read hole).
        var contractor = $"c-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, contractor, "Contractor");
        var unknown = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(contractor));
        Assert.Equal(HttpStatusCode.Forbidden, (await unknown.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockId, gradeId, eggs: 20))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await unknown.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Nope", phone = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await unknown.GetAsync("/api/v1/stock")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await unknown.GetAsync("/api/v1/flocks")).StatusCode);
    }

    // Voiding a payment is the corrective tier — a Sales user must not void
    // their own recorded payment (#104 panel: pocket the cash, reopen the
    // balance, no Owner/Manager in the loop).
    [Fact]
    public async Task Sales_CanRecordPayments_ButNotVoidThem()
    {
        var (accountId, farmId, flockId, gradeId) = await SeedFarmAsync();
        var productId = await factory.SeedProductAsync(accountId, farmId, gradeId, "Large Eggs", 100);
        await factory.SeedEggLotAsync(accountId, gradeId, 100);
        var sales = await ClientAsync(accountId, Roles.Sales);

        var customer = await sales.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Void Buyer", phone = "1" });
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await sales.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;
        await sales.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 10 });
        Assert.Equal(HttpStatusCode.OK, (await sales.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString())).StatusCode);
        var pay = await sales.PostWithKeyAsync($"/api/v1/sales/{orderId}/payments",
            Guid.NewGuid().ToString(), new { paymentDate = Today, amountMinorUnits = 500, method = "Cash" });
        Assert.Equal(HttpStatusCode.Created, pay.StatusCode);
        var paymentId = (await pay.Content.ReadFromJsonAsync<PaymentCreated>())!.Id;

        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostWithKeyAsync(
            $"/api/v1/payments/{paymentId}/void", Guid.NewGuid().ToString(),
            new { version = 0, reason = "mine" })).StatusCode);
    }

    // The remaining scope surfaces: feed usage shares the guard, an
    // out-of-scope DRAFT cannot be submitted after unassignment, and a
    // mismatched user/assignment pair deletes nothing.
    [Fact]
    public async Task FlockScope_CoversFeed_SubmitOfUnassignedDraft_AndMismatchedUnassign()
    {
        var (accountId, farmId, flockA, gradeId) = await SeedFarmAsync();
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var owner = await ClientAsync(accountId, Roles.Owner);
        var workerEmail = $"w2-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, (string?)null);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));
        var workerId = (await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users"))!
            .Single(u => u.Email == workerEmail).Id;

        // Draft on flock B while unscoped, then narrow to flock A.
        var draft = await worker.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockB, gradeId));
        var draftId = (await draft.Content.ReadFromJsonAsync<Created>())!.Id;
        await owner.PostWithKeyAsync($"/api/v1/users/{workerId}/flock-assignments",
            Guid.NewGuid().ToString(), new { flockId = flockA });

        // Submitting the now-out-of-scope draft is refused; the owner can.
        var submit = await worker.PostWithKeyAsync(
            $"/api/v1/daily-entries/{draftId}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, submit.StatusCode);
        Assert.Contains("FlockScope.NotAssigned", await submit.Content.ReadAsStringAsync());

        // Feed usage shares the guard: item with stock, worker uses on flock B.
        var item = await owner.PostWithKeyAsync("/api/v1/inventory/items", Guid.NewGuid().ToString(),
            new { name = $"Feed-{Guid.NewGuid():N}"[..12], category = "Feed", unit = "kg" });
        var itemId = (await item.Content.ReadFromJsonAsync<Created>())!.Id;
        await owner.PostWithKeyAsync($"/api/v1/inventory/items/{itemId}/purchases",
            Guid.NewGuid().ToString(),
            new { receivedDate = Today, quantity = 100, unitCostMinorUnits = 50 });
        var feed = await worker.PostWithKeyAsync($"/api/v1/inventory/items/{itemId}/usage",
            Guid.NewGuid().ToString(), new { flockId = flockB, date = Today, quantity = 5 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, feed.StatusCode);
        Assert.Contains("FlockScope.NotAssigned", await feed.Content.ReadAsStringAsync());

        // Mismatched user/assignment pair: 404, nothing deleted.
        var otherWorker = $"w3-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, otherWorker, (string?)null);
        var otherId = (await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users"))!
            .Single(u => u.Email == otherWorker).Id;
        var assignments = await owner.GetFromJsonAsync<List<AssignmentRow>>(
            $"/api/v1/users/{workerId}/flock-assignments");
        var assignmentId = Assert.Single(assignments!).Id;
        var mismatched = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/v1/users/{otherId}/flock-assignments/{assignmentId}");
        mismatched.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.SendAsync(mismatched)).StatusCode);
        Assert.Single((await owner.GetFromJsonAsync<List<AssignmentRow>>(
            $"/api/v1/users/{workerId}/flock-assignments"))!);
    }

    private sealed record PaymentCreated(Guid Id);
    private sealed record PaymentItem(Guid Id, int Version);
    private sealed record PaymentsPage(List<PaymentItem> Items);

    [Fact]
    public async Task Assignments_AreTenantIsolated()
    {
        var (accountA, _, flockA, _) = await SeedFarmAsync();
        var ownerA = await ClientAsync(accountA, Roles.Owner);

        var (accountB, _, _, _) = await SeedFarmAsync();
        var ownerB = await ClientAsync(accountB, Roles.Owner);
        var workerB = $"wb-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountB, workerB, (string?)null);
        var workerBId = (await ownerB.GetFromJsonAsync<List<UserRow>>("/api/v1/users"))!
            .Single(u => u.Email == workerB).Id;

        // A's owner cannot assign to B's user; B's owner cannot use A's flock.
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.PostWithKeyAsync(
            $"/api/v1/users/{workerBId}/flock-assignments", Guid.NewGuid().ToString(),
            new { flockId = flockA })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PostWithKeyAsync(
            $"/api/v1/users/{workerBId}/flock-assignments", Guid.NewGuid().ToString(),
            new { flockId = flockA })).StatusCode);
    }

    private static HttpRequestMessage WithKey(HttpRequestMessage request)
    {
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return request;
    }

    private sealed record UserRow(Guid Id, string Email, string? DisplayName, string Role);
    private sealed record AssignmentRow(Guid Id, Guid? FlockId);
}
