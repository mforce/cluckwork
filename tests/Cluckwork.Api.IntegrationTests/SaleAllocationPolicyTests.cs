namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Repositories;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #612 — the confirmation-time policy application: whole-order planning tries
// a restricted plain Worker's assigned flocks first under AssignedFlocksOnly,
// falls back to the SAME locked farm-wide rows, and picks between the two
// distinct 422 codes without ever mutating on a failed attempt. Every other
// caller shape (elevated role, farm-wide policy, unrestricted Worker) keeps
// today's plain farm-wide behavior and today's specific error message.
[Collection(IntegrationCollection.Name)]
public sealed class SaleAllocationPolicyTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private sealed record Created(Guid Id);
    private sealed record UserRow(Guid Id, string Email, string? DisplayName, string Role);
    private sealed record ProblemDto(string? Title, string? Detail);
    private sealed record StepUpDto(string Token, DateTimeOffset ExpiresAt);

    private async Task<(Guid AccountId, HttpClient Owner, Guid FarmId, Guid GradeId, Guid ProductId)> SeedFarmAsync()
    {
        var ownerEmail = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        var owner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(ownerEmail));
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var gradeId = grades["Large"];
        var productId = await factory.SeedProductAsync(accountId, farmId, gradeId, "Large Eggs", 100);
        return (accountId, owner, farmId, gradeId, productId);
    }

    // productionDate defaults to Today; FIFO orders by (ProductionDate, Id), so
    // any test that asserts WHICH lot a draw comes from first must pass an
    // explicit, distinct date — two same-day lots break ties by Id, which is
    // an arbitrary fresh Guid.
    private async Task<Guid> SeedLotAsync(
        Guid accountId, Guid flockId, Guid gradeId, int quantity, DateOnly? productionDate = null)
    {
        var lotId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var lot = EggLot.Create(lotId, accountId, flockId, productionDate ?? Today, gradeId, quantity);
            db.EggInventoryMovements.Add(EggInventoryMovement.Create(
                Guid.NewGuid(), accountId, lotId, EggMovementType.Production,
                quantity, "DailyEntry", Guid.NewGuid(), DateTimeOffset.UtcNow));
            db.EggLots.Add(lot);
            await db.SaveChangesAsync();
        });
        return lotId;
    }

    private async Task<Guid> CreateOrderAsync(HttpClient client, Guid productId, int quantity)
    {
        var customer = await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = $"Buyer {Guid.NewGuid():N}"[..14], phone = "1" });
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.Created, (await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity })).StatusCode);
        return orderId;
    }

    private async Task SetPolicyAsync(Guid accountId, WorkerSaleAllocationPolicy policy) =>
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var account = await db.Accounts.SingleAsync();
            var result = account.UpdateSettings(
                account.Name, account.TimeZoneId, account.Locale, account.DefaultCurrencyCode,
                account.UnitSystem, account.FirstDayOfWeek, account.DateFormatOverride, account.TimeFormatOverride,
                account.Brand, account.DefaultStepperUnit, policy, financialRowsExist: false);
            Assert.True(result.IsSuccess);
            await db.SaveChangesAsync();
        });

    private async Task<(HttpClient Client, Guid UserId)> SeedWorkerAsync(Guid accountId, HttpClient owner)
    {
        var email = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, (string?)null);
        var userId = (await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users"))!
            .Single(u => u.Email == email).Id;
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, userId);
    }

    private static async Task<string> StepUpAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/step-up", new { password = TestHarness.Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StepUpDto>())!.Token;
    }

    private static async Task<HttpResponseMessage> AssignFlockAsync(HttpClient client, Guid userId, Guid flockId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/users/{userId}/flock-assignments")
        { Content = JsonContent.Create(new { flockId }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(AuthEndpoints.StepUpHeaderName, await StepUpAsync(client));
        return await client.SendAsync(request);
    }

    private async Task<(int QuantityA, int QuantityB, string Status, int Version)> SnapshotAsync(
        Guid accountId, Guid lotA, Guid lotB, Guid orderId) =>
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var qa = (await db.EggLots.AsNoTracking().SingleAsync(l => l.Id == lotA)).QuantityAvailable;
            var qb = (await db.EggLots.AsNoTracking().SingleAsync(l => l.Id == lotB)).QuantityAvailable;
            var order = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            return (qa, qb, order.Status.ToString(), order.Version);
        });

    // --- default assigned-only, sufficient assigned stock ------------------

    [Fact]
    public async Task DefaultPolicy_RestrictedWorker_SufficientAssignedStock_AllocatesFromAssignedFlockOnly()
    {
        var (accountId, owner, farmId, gradeId, productId) = await SeedFarmAsync();
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var lotA = await SeedLotAsync(accountId, flockA, gradeId, 20);
        var lotB = await SeedLotAsync(accountId, flockB, gradeId, 20);

        var (worker, workerId) = await SeedWorkerAsync(accountId, owner);
        Assert.Equal(HttpStatusCode.Created, (await AssignFlockAsync(owner, workerId, flockA)).StatusCode);

        var orderId = await CreateOrderAsync(worker, productId, 10);
        var response = await worker.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (qa, qb, status, _) = await SnapshotAsync(accountId, lotA, lotB, orderId);
        Assert.Equal(10, qa);   // drawn from the assigned flock
        Assert.Equal(20, qb);   // unassigned flock untouched
        Assert.Equal("Confirmed", status);
    }

    // --- assigned failure / farm success: distinct 422, nothing applied ----

    [Fact]
    public async Task DefaultPolicy_RestrictedWorker_AssignedInsufficient_FarmWideSufficient_Returns422DistinctCode_AndAppliesNothing()
    {
        var (accountId, owner, farmId, gradeId, productId) = await SeedFarmAsync();
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var lotA = await SeedLotAsync(accountId, flockA, gradeId, 5, Today.AddDays(-1));
        var lotB = await SeedLotAsync(accountId, flockB, gradeId, 20, Today);

        var (worker, workerId) = await SeedWorkerAsync(accountId, owner);
        Assert.Equal(HttpStatusCode.Created, (await AssignFlockAsync(owner, workerId, flockA)).StatusCode);

        var orderId = await CreateOrderAsync(worker, productId, 10);
        var before = await SnapshotAsync(accountId, lotA, lotB, orderId);

        var response = await worker.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.Equal("EggLot.AssignedFlocksInsufficientStock", problem!.Title);
        // Generic — no grade name, no quantity, no flock fact leaked.
        Assert.DoesNotContain("Large", problem.Detail);
        Assert.DoesNotContain("20", problem.Detail);
        // #612 review fix — explicitly names the Owner/Manager opt-in and where
        // to find it, without naming what stock exists or where.
        Assert.Contains("owner or manager", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Farm settings", problem.Detail, StringComparison.OrdinalIgnoreCase);

        var after = await SnapshotAsync(accountId, lotA, lotB, orderId);
        Assert.Equal(before, after); // exact SalesOrder/EggLot state unchanged
    }

    // --- both insufficient: existing code, generic message for a restricted worker

    [Fact]
    public async Task DefaultPolicy_RestrictedWorker_BothInsufficient_ReturnsExistingCode_WithAGenericMessage()
    {
        var (accountId, owner, farmId, gradeId, productId) = await SeedFarmAsync();
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var lotA = await SeedLotAsync(accountId, flockA, gradeId, 5);
        var lotB = await SeedLotAsync(accountId, flockB, gradeId, 2);

        var (worker, workerId) = await SeedWorkerAsync(accountId, owner);
        Assert.Equal(HttpStatusCode.Created, (await AssignFlockAsync(owner, workerId, flockA)).StatusCode);

        var orderId = await CreateOrderAsync(worker, productId, 10);
        var before = await SnapshotAsync(accountId, lotA, lotB, orderId);

        var response = await worker.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.Equal("EggLot.InsufficientStock", problem!.Title);
        // Generic for a restricted Worker — must not reveal the farm-wide
        // shortfall (grade name / remaining count) it cannot see the source of.
        Assert.DoesNotContain("Large", problem.Detail);
        Assert.DoesNotContain("unallocated", problem.Detail);

        var after = await SnapshotAsync(accountId, lotA, lotB, orderId);
        Assert.Equal(before, after);
    }

    // --- opt-in farm-wide policy --------------------------------------------

    [Fact]
    public async Task AllFarmFlocksPolicy_RestrictedWorker_AllocatesAcrossBothFlocks()
    {
        var (accountId, owner, farmId, gradeId, productId) = await SeedFarmAsync();
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var lotA = await SeedLotAsync(accountId, flockA, gradeId, 5, Today.AddDays(-1));
        var lotB = await SeedLotAsync(accountId, flockB, gradeId, 20, Today);
        await SetPolicyAsync(accountId, WorkerSaleAllocationPolicy.AllFarmFlocks);

        var (worker, workerId) = await SeedWorkerAsync(accountId, owner);
        Assert.Equal(HttpStatusCode.Created, (await AssignFlockAsync(owner, workerId, flockA)).StatusCode);

        var orderId = await CreateOrderAsync(worker, productId, 10);
        var response = await worker.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (qa, qb, status, _) = await SnapshotAsync(accountId, lotA, lotB, orderId);
        Assert.Equal(0, qa);    // assigned flock's 5 drawn first (FIFO)
        Assert.Equal(15, qb);  // remaining 5 drawn from the unassigned flock
        Assert.Equal("Confirmed", status);
    }

    // #612 review fix — AllFarmFlocks lets a restricted Worker's confirmation
    // draw from any flock, but on a farm-wide SHORTFALL it must get the SAME
    // generic message as every other restricted-Worker failure, not the
    // grade/quantity detail elevated roles see. Before the fix this fell
    // through to the detailed branch because it is reached only via
    // AssignedFlocksOnly's own retry.
    [Fact]
    public async Task AllFarmFlocksPolicy_RestrictedWorker_FarmWideInsufficient_ReturnsGenericMessage()
    {
        var (accountId, owner, farmId, gradeId, productId) = await SeedFarmAsync();
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var lotA = await SeedLotAsync(accountId, flockA, gradeId, 5);
        var lotB = await SeedLotAsync(accountId, flockB, gradeId, 2);
        await SetPolicyAsync(accountId, WorkerSaleAllocationPolicy.AllFarmFlocks);

        var (worker, workerId) = await SeedWorkerAsync(accountId, owner);
        Assert.Equal(HttpStatusCode.Created, (await AssignFlockAsync(owner, workerId, flockA)).StatusCode);

        var orderId = await CreateOrderAsync(worker, productId, 10);
        var before = await SnapshotAsync(accountId, lotA, lotB, orderId);

        var response = await worker.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.Equal("EggLot.InsufficientStock", problem!.Title);
        // Generic even under AllFarmFlocks — no grade name, no remaining count.
        Assert.DoesNotContain("Large", problem.Detail);
        Assert.DoesNotContain("unallocated", problem.Detail);

        var after = await SnapshotAsync(accountId, lotA, lotB, orderId);
        Assert.Equal(before, after);
    }

    // --- Account lock genuinely serializes a policy change ------------------

    // #612 required proof: "Policy and role changes serialize through the
    // Account lock." ConfirmSaleHandler's Account read is the SAME
    // GetCurrentSharedLockedAsync (FOR SHARE) call CurrencyLockRaceTests
    // already races every OTHER stamping handler against a FOR UPDATE
    // settings write — this pins it for Confirm specifically, and proves not
    // just blocking but CORRECTNESS: the handler must read the committed new
    // policy, not a stale one, so the outcome (which flock the stock comes
    // from) flips on which policy actually won the race.
    //
    // A separate role-change race would exercise the identical Account lock
    // statement (the fresh identity.GetEffectiveRoleAsync re-check runs
    // strictly AFTER this same FOR SHARE acquisition, inside the same
    // transaction) — this one test already proves that acquisition itself
    // serializes, so a second copy would add no new coverage.
    [Fact]
    public async Task ConfirmSale_ParksOnTheAccountLock_AndReadsAPolicyChangeThatCommittedWhileItWaited()
    {
        var (accountId, owner, farmId, gradeId, productId) = await SeedFarmAsync();
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        // Assigned flock alone is insufficient; farm-wide is sufficient — the
        // outcome below only makes sense if the handler read AllFarmFlocks.
        var lotA = await SeedLotAsync(accountId, flockA, gradeId, 5, Today.AddDays(-1));
        var lotB = await SeedLotAsync(accountId, flockB, gradeId, 20, Today);

        var (worker, workerId) = await SeedWorkerAsync(accountId, owner);
        Assert.Equal(HttpStatusCode.Created, (await AssignFlockAsync(owner, workerId, flockA)).StatusCode);
        var orderId = await CreateOrderAsync(worker, productId, 10);

        // The fence: an exclusive lock on the Account row, standing in for an
        // in-flight settings write that has not committed yet. Built
        // directly, not via factory.Services — #269, same reason
        // CurrencyLockRaceTests builds its own fence connection.
        var tenantA = new TenantContext();
        tenantA.Resolve(accountId);
        await using var dbA = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenantA, new FlockScope());
        await using var transactionA = await dbA.Database.BeginTransactionAsync();
        await dbA.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""");
        await dbA.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Accounts" SET "WorkerSaleAllocationPolicy" = 'AllFarmFlocks', "Version" = "Version" + 1 WHERE "Id" = {accountId}""");
        var holderPid = await dbA.BackendPidAsync();

        var confirm = worker.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        var blocked = await factory.WaitUntilDoneOrBlockedAsync(confirm, holderPid);
        Assert.True(blocked, "ConfirmSaleHandler must park on the account row's shared lock, not read the stale policy");

        await transactionA.CommitAsync();
        var response = await confirm;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (qa, qb, status, _) = await SnapshotAsync(accountId, lotA, lotB, orderId);
        Assert.Equal(0, qa);
        Assert.Equal(15, qb); // drew farm-wide — proves it read the COMMITTED new policy
        Assert.Equal("Confirmed", status);
    }

    // Review r3887146493 — the same Account-lock window, but the actor is
    // DISABLED while parked. GetEffectiveRoleAsync's admission check asked only
    // whether the user EXISTS in the account, and a disabled user's role rows
    // survive (only auth is blocked — #355), so step 5's "fresh read" returned
    // the queued authority and the confirm completed: 200, stock decremented,
    // order Confirmed, by a caller whose access was revoked before it ran.
    // Membership is not authority; the predicate is ACTIVE membership.
    //
    // Red-before-green: with the DisabledAt == null predicate removed this
    // fails on the very first assertion with OK instead of Forbidden, and the
    // snapshot comparison then fails too (10 eggs drawn, status Confirmed).
    [Fact]
    public async Task ConfirmSale_ParkedOnTheAccountLock_IsForbidden_WhenTheActorIsDisabledWhileItWaits()
    {
        var (accountId, owner, farmId, gradeId, productId) = await SeedFarmAsync();
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        // Deliberately SUFFICIENT on the assigned flock under the default
        // AssignedFlocksOnly: the only thing that can refuse this confirm is
        // the actor check, so a 200 here is unambiguously the stale-actor bug
        // and not a shortfall.
        var lotA = await SeedLotAsync(accountId, flockA, gradeId, 20, Today.AddDays(-1));
        var lotB = await SeedLotAsync(accountId, flockB, gradeId, 20, Today);

        var (worker, workerId) = await SeedWorkerAsync(accountId, owner);
        Assert.Equal(HttpStatusCode.Created, (await AssignFlockAsync(owner, workerId, flockA)).StatusCode);
        var orderId = await CreateOrderAsync(worker, productId, 10);
        var before = await SnapshotAsync(accountId, lotA, lotB, orderId);

        // Same fence as the policy race above: an exclusive Account-row lock
        // standing in for an in-flight settings write, built directly (#269).
        var tenantA = new TenantContext();
        tenantA.Resolve(accountId);
        await using var dbA = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenantA, new FlockScope());
        await using var transactionA = await dbA.Database.BeginTransactionAsync();
        await dbA.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""");
        var holderPid = await dbA.BackendPidAsync();

        // The request authenticates BEFORE it parks — this is the window the
        // fix closes: middleware has already passed, the disable lands after.
        var confirm = worker.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        var blocked = await factory.WaitUntilDoneOrBlockedAsync(confirm, holderPid);
        Assert.True(blocked, "ConfirmSaleHandler must park on the account row's shared lock");

        // Disable from a SEPARATE DbContext, committed while the confirm waits.
        // It touches AspNetUsers, never Accounts, so it does not queue behind
        // the fence — the row is committed-disabled before the confirm resumes.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == workerId);
            user.DisabledAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });

        await transactionA.CommitAsync();
        var response = await confirm;

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = await SnapshotAsync(accountId, lotA, lotB, orderId);
        Assert.Equal(before, after);
    }

    // --- zero/null assignments: unrestricted regardless of policy -----------

    [Fact]
    public async Task DefaultPolicy_WorkerWithNoAssignments_AllocatesFarmWide()
    {
        var (accountId, owner, farmId, gradeId, productId) = await SeedFarmAsync();
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var lotA = await SeedLotAsync(accountId, flockA, gradeId, 5, Today.AddDays(-1));
        var lotB = await SeedLotAsync(accountId, flockB, gradeId, 20, Today);

        var (worker, _) = await SeedWorkerAsync(accountId, owner); // 0 assignment rows

        var orderId = await CreateOrderAsync(worker, productId, 10);
        var response = await worker.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (qa, qb, status, _) = await SnapshotAsync(accountId, lotA, lotB, orderId);
        Assert.Equal(0, qa);
        Assert.Equal(15, qb);
        Assert.Equal("Confirmed", status);
    }

    [Fact]
    public async Task DefaultPolicy_WorkerWithAFarmWideAssignmentRow_AllocatesFarmWide()
    {
        var (accountId, owner, farmId, gradeId, productId) = await SeedFarmAsync();
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var lotA = await SeedLotAsync(accountId, flockA, gradeId, 5, Today.AddDays(-1));
        var lotB = await SeedLotAsync(accountId, flockB, gradeId, 20, Today);

        var (worker, workerId) = await SeedWorkerAsync(accountId, owner);
        // Farm-wide row (FlockId null) — the assignment API rejects
        // Guid.Empty, so seed it directly, same as FlockScopeMiddlewareTests.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(
                Guid.NewGuid(), accountId, workerId, farmId, houseId: null, flockId: null));
            await db.SaveChangesAsync();
        });

        var orderId = await CreateOrderAsync(worker, productId, 10);
        var response = await worker.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (qa, qb, _, _) = await SnapshotAsync(accountId, lotA, lotB, orderId);
        Assert.Equal(0, qa);
        Assert.Equal(15, qb);
    }

    // --- elevated roles keep today's farm-wide behavior, even with a retained row

    [Theory]
    [InlineData(Roles.Manager)]
    [InlineData(Roles.Sales)]
    public async Task DefaultPolicy_ElevatedRoleWithARetainedAssignmentRow_StillAllocatesFarmWide(string role)
    {
        var (accountId, owner, farmId, gradeId, productId) = await SeedFarmAsync();
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var lotA = await SeedLotAsync(accountId, flockA, gradeId, 5, Today.AddDays(-1));
        var lotB = await SeedLotAsync(accountId, flockB, gradeId, 20, Today);

        // Assign while still a Worker (a live assignment write now requires
        // it), then promote — #612's actual "retained but inert" scenario.
        var (client, userId) = await SeedWorkerAsync(accountId, owner);
        Assert.Equal(HttpStatusCode.Created, (await AssignFlockAsync(owner, userId, flockA)).StatusCode);
        var roleChange = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{userId}/role")
        { Content = JsonContent.Create(new { role }) };
        roleChange.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        roleChange.Headers.Add(AuthEndpoints.StepUpHeaderName, await StepUpAsync(owner));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(roleChange)).StatusCode);

        var elevated = factory.CreateAuthedClient(
            await factory.LoginForAccessTokenAsync((await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users"))!
                .Single(u => u.Id == userId).Email));

        var orderId = await CreateOrderAsync(elevated, productId, 10);
        var response = await elevated.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (qa, qb, _, _) = await SnapshotAsync(accountId, lotA, lotB, orderId);
        Assert.Equal(0, qa);
        Assert.Equal(15, qb); // drew from BOTH flocks — never restricted
    }

    // --- a role that changes to a now-forbidden one between auth and the ---
    // --- Account lock is refused with 403, not folded into the 409/422 -----
    // Route policy (SalesFlow) already excludes ReadOnly, so this exercises
    // the handler's OWN fresh re-check directly, bypassing HTTP routing —
    // the defense-in-depth #612 step 5 asks for.
    [Fact]
    public async Task ConfirmSaleHandler_ReadOnlyActingUser_IsForbidden_RegardlessOfRoutePolicy()
    {
        var (accountId, owner, farmId, gradeId, productId) = await SeedFarmAsync();
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        await SeedLotAsync(accountId, flockA, gradeId, 20);
        var orderId = await CreateOrderAsync(owner, productId, 10);

        var readOnlyEmail = $"ro-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, readOnlyEmail, Roles.ReadOnly);
        var readOnlyId = (await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users"))!
            .Single(u => u.Email == readOnlyEmail).Id;

        using var scope = factory.Services.CreateScope();
        scope.ResolveTenantAndActor(accountId, readOnlyId, readOnlyEmail, roles: [Roles.ReadOnly]);
        var handler = scope.ServiceProvider
            .GetRequiredService<Cluckwork.Application.Features.Sales.ConfirmSale.ConfirmSaleHandler>();

        var result = await handler.HandleAsync(
            new Cluckwork.Application.Features.Sales.ConfirmSale.ConfirmSaleCommand(orderId),
            accountId, readOnlyId, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.Forbidden", result.Error.Code);

        var order = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId));
        Assert.Equal(Cluckwork.Domain.Sales.SalesOrderStatus.Draft, order.Status);
    }

    // --- exactly one FIFO query, proven by a counting repository spy --------

    // Static, not per-instance: EF resolves a fresh repository (and DbContext)
    // per request scope, so the count must live outside any one instance to
    // survive across the HTTP call this test drives.
    private static int FifoQueryCount;

    private sealed class CountingEggLotRepository(IEggLotRepository inner) : IEggLotRepository
    {
        public Task<IReadOnlyList<EggLot>> GetAvailableFifoLockedAsync(
            Guid accountId, IReadOnlyList<Guid> eggGradeIds, DateOnly allocationDate,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref FifoQueryCount);
            return inner.GetAvailableFifoLockedAsync(accountId, eggGradeIds, allocationDate, ct);
        }

        public Task<IReadOnlyList<StockByGrade>> GetStockByGradeAsync(DateOnly asOfDate, CancellationToken ct = default) =>
            inner.GetStockByGradeAsync(asOfDate, ct);
        public Task<IReadOnlyList<EggLot>> GetByIdsLockedAsync(Guid accountId, IReadOnlyList<Guid> lotIds, CancellationToken ct = default) =>
            inner.GetByIdsLockedAsync(accountId, lotIds, ct);
        public Task<IReadOnlyList<EggLot>> GetByDailyEntryLockedAsync(Guid accountId, Guid dailyEntryId, CancellationToken ct = default) =>
            inner.GetByDailyEntryLockedAsync(accountId, dailyEntryId, ct);
        public Task<IReadOnlyList<EggLot>> ListAsync(Guid? eggGradeId, DateOnly? from, DateOnly? to, int limit, int offset, CancellationToken ct = default) =>
            inner.ListAsync(eggGradeId, from, to, limit, offset, ct);
        public Task<EggLot?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);
        public Task AddAsync(EggLot entity, CancellationToken ct = default) => inner.AddAsync(entity, ct);
        public void Update(EggLot entity) => inner.Update(entity);
        public void Remove(EggLot entity) => inner.Remove(entity);
    }

    [Fact]
    public async Task ConfirmSale_RestrictedWorkerAssignedRetry_IssuesExactlyOneFifoQuery()
    {
        FifoQueryCount = 0;
        var spied = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddScoped<IEggLotRepository>(sp =>
                new CountingEggLotRepository(new EggLotRepository(sp.GetRequiredService<AppDbContext>())))));

        // Seed/login through the ORIGINAL factory (same underlying database);
        // only the CLIENTS that actually make the confirm request need to be
        // built against the DI-swapped host below.
        var (accountId, owner, farmId, gradeId, productId) = await SeedFarmAsync();
        var ownerToken = owner.DefaultRequestHeaders.Authorization!.Parameter!;
        owner = spied.CreateClient();
        owner.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ownerToken);

        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        await SeedLotAsync(accountId, flockA, gradeId, 5);   // insufficient assigned
        await SeedLotAsync(accountId, flockB, gradeId, 20);  // sufficient farm-wide

        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, (string?)null);
        var workerId = (await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users"))!
            .Single(u => u.Email == workerEmail).Id;
        var workerToken = await factory.LoginForAccessTokenAsync(workerEmail);
        var worker = spied.CreateClient();
        worker.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", workerToken);
        Assert.Equal(HttpStatusCode.Created, (await AssignFlockAsync(owner, workerId, flockA)).StatusCode);

        var customer = await worker.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Spy Buyer", phone = "1" });
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await worker.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;
        await worker.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 10 });

        var response = await worker.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode); // assigned-then-farm-wide retry path

        // The assigned-only plan failed and the SAME locked rows were replanned
        // farm-wide — proving that retry never re-queries or re-locks.
        Assert.Equal(1, FifoQueryCount);
    }
}
