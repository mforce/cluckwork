namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;

// #388 — FlockScopeResolutionMiddleware: resolution outcomes per persona, and
// the single-assignment contract. Scoping itself (filtered reads) is asserted
// in FlockScopeTests (Increment 2).
[Collection(IntegrationCollection.Name)]
public sealed class FlockScopeMiddlewareTests(CluckworkWebApplicationFactory factory)
{
    // Fixture: one farm, flocks A + B, an Owner, a Manager, a worker with 0
    // assignments, a worker assigned flock A, a worker with a farm-wide row.
    // (See RoleMatrixTests for the verbatim seeding + assignment patterns.)

    [Fact]
    public async Task Owner_Request_Completes_ScopeUnrestricted()
    {
        var (accountId, farmId, flockA, _) = await SeedFarmAsync();
        var owner = await ClientAsync(accountId, Roles.Owner);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/flocks")).StatusCode);

        var scope = new FlockScope();
        scope.Resolve(true, []);
        Assert.True(scope.IsUnrestricted);
        Assert.True(scope.IsResolved);
    }

    [Fact]
    public async Task Manager_Request_Completes_ScopeUnrestricted()
    {
        var (accountId, farmId, flockA, _) = await SeedFarmAsync();
        var manager = await ClientAsync(accountId, Roles.Manager);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/v1/flocks")).StatusCode);

        var scope = new FlockScope();
        scope.Resolve(true, []);
        Assert.True(scope.IsUnrestricted);
        Assert.True(scope.IsResolved);
    }

    [Fact]
    public async Task Worker_ZeroAssignments_Request_Succeeds()
    {
        var (accountId, farmId, flockA, _) = await SeedFarmAsync();
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var worker = await ClientAsync(accountId, (string?)null);
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync("/api/v1/flocks")).StatusCode);

        // M4 strengthen (mutation-check record): a 0-assignment worker is
        // UNRESTRICTED (grandfathered #73, matches FlockScopeGuard line 80), so
        // BOTH seeded flocks must be visible in its list — not an empty
        // restricted scope. Guards a Resolve(false, []) in the 0-assignment
        // branch (which would empty every read).
        var flocks = await (await worker.GetAsync("/api/v1/flocks"))
            .Content.ReadFromJsonAsync<List<FlockRow>>();
        Assert.NotNull(flocks);
        Assert.Contains(flocks, f => f.Id == flockA);
        Assert.Contains(flocks, f => f.Id == flockB);
    }

    [Fact]
    public async Task Worker_SingleFlockAssignment_Request_Succeeds()
    {
        var (accountId, farmId, flockA, _) = await SeedFarmAsync();
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var ownerEmail = $"o-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, ownerEmail, Roles.Owner);
        var owner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(ownerEmail));

        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, (string?)null);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));
        var workerId = (await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users"))!
            .Single(u => u.Email == workerEmail).Id;

        Assert.Equal(HttpStatusCode.Created, (await AssignFlockAsync(owner, workerId, flockA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync("/api/v1/flocks")).StatusCode);
    }

    [Fact]
    public async Task Worker_FarmWideRow_Request_Succeeds()
    {
        var (accountId, farmId, flockA, _) = await SeedFarmAsync();
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var ownerEmail = $"o-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, ownerEmail, Roles.Owner);
        var owner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(ownerEmail));

        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, (string?)null);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));
        var workerId = (await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users"))!
            .Single(u => u.Email == workerEmail).Id;

        // Farm-wide assignment row: FlockId=null. The assignment API rejects
        // Guid.Empty (AssignFlockRequest(Guid FlockId)), so it cannot create a
        // farm-wide row — seed it DIRECTLY instead. A non-null farmId satisfies
        // UserRoleAssignment.Create's invariant (farm/house/flock, at least one).
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(
                Guid.NewGuid(), accountId, workerId, farmId, houseId: null, flockId: null));
            await db.SaveChangesAsync();
        });

        var response = await worker.GetAsync("/api/v1/flocks");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // M5 strengthen (mutation-check record): a farm-wide (FlockId=null) row
        // grants everything (matches FlockScopeGuard line 84), so BOTH flocks
        // must be visible — not an empty restricted scope. Guards removing the
        // farm-wide branch entirely (which would fall through to Resolve(false,
        // []) and empty every read).
        var flocks = await response.Content.ReadFromJsonAsync<List<FlockRow>>();
        Assert.NotNull(flocks);
        Assert.Contains(flocks, f => f.Id == flockA);
        Assert.Contains(flocks, f => f.Id == flockB);
    }

    [Fact]
    public async Task UnauthenticatedRequest_DoesNotResolveScope()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/flocks")).StatusCode);
    }

    [Fact]
    public void Resolve_SameScopeTwice_IsNoOp()
    {
        var scope = new FlockScope();
        scope.Resolve(true, []);
        scope.Resolve(true, []); // must not throw
        Assert.True(scope.IsUnrestricted);
        Assert.True(scope.IsResolved);
    }

    [Fact]
    public void Resolve_DifferingScope_ThrowsReassignmentException()
    {
        var scope = new FlockScope();
        var flockA = Guid.NewGuid();
        var flockB = Guid.NewGuid();
        scope.Resolve(false, [flockA]);
        Assert.Throws<FlockScopeReassignmentException>(() => scope.Resolve(false, [flockB]));
        // Same set, different order: no-op.
        var scope2 = new FlockScope();
        scope2.Resolve(false, [flockA, flockB]);
        scope2.Resolve(false, [flockB, flockA]); // must not throw
    }

    [Fact]
    public void Resolve_SameSetDifferentOrder_IsNoOp()
    {
        var flockA = Guid.NewGuid();
        var flockB = Guid.NewGuid();
        var scope = new FlockScope();
        scope.Resolve(false, [flockA, flockB]);
        scope.Resolve(false, [flockB, flockA]); // must not throw
        Assert.Equal(2, scope.AssignedFlockIds.Count);
    }

    [Fact]
    public void Resolve_UnrestrictedToRestricted_Throws()
    {
        var scope = new FlockScope();
        scope.Resolve(true, []);
        Assert.Throws<FlockScopeReassignmentException>(() => scope.Resolve(false, [Guid.NewGuid()]));
    }

    // --- private helpers, verbatim patterns from RoleMatrixTests.cs ---
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

    private sealed record StepUpDto(string Token, DateTimeOffset ExpiresAt);

    private static async Task<string> StepUpAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/step-up", new { password = TestHarness.Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StepUpDto>())!.Token;
    }

    private static async Task<HttpResponseMessage> AssignFlockAsync(
        HttpClient client, Guid userId, Guid flockId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/users/{userId}/flock-assignments")
        {
            Content = JsonContent.Create(new { flockId }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(AuthEndpoints.StepUpHeaderName, await StepUpAsync(client));
        return await client.SendAsync(request);
    }

    private sealed record UserRow(Guid Id, string Email, string? DisplayName, string Role);
    private sealed record FlockRow(Guid Id, Guid FarmId, Guid HouseId, string Name, string Breed);
}
