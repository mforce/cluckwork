namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

// #73 — Admin vs not-Admin. The principle under test: anything that undoes,
// corrects, or reconfigures is admin-only; recording the day's work is open
// to any authenticated user. The SPA hides gated controls, but these tests
// prove the API refuses regardless.
[Collection(IntegrationCollection.Name)]
public sealed class AdminGatingTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record UserRow(Guid Id, string Email, string? DisplayName, string Role);
    private sealed record StepUpDto(string Token, DateTimeOffset ExpiresAt);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private async Task<(HttpClient Admin, HttpClient Worker, Guid AccountId, Guid FarmId, Guid FlockId)>
        SetupAsync()
    {
        var adminEmail = $"admin-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(adminEmail);
        var workerEmail = $"worker-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);

        var farmId = Guid.NewGuid();
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var admin = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(adminEmail));
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));
        return (admin, worker, accountId, farmId, flockId);
    }

    private static Task<HttpResponseMessage> SendWithKeyAsync(
        HttpClient client, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Worker_CorrectiveEndpoints_AreForbidden()
    {
        var (_, worker, _, _, flockId) = await SetupAsync();
        var id = Guid.NewGuid();

        // Authorization short-circuits before binding/validation, so invented
        // ids and empty bodies still exercise the gate itself.
        (HttpMethod Method, string Url)[] gated =
        [
            (HttpMethod.Put, $"/api/v1/flocks/{flockId}"),
            (HttpMethod.Post, $"/api/v1/flocks/{flockId}/deplete"),
            (HttpMethod.Post, $"/api/v1/flocks/{flockId}/archive"),
            (HttpMethod.Post, $"/api/v1/flocks/{flockId}/reactivate"),
            (HttpMethod.Post, $"/api/v1/flocks/{flockId}/movements"),
            (HttpMethod.Post, "/api/v1/egg-grades"),
            (HttpMethod.Put, $"/api/v1/egg-grades/{id}"),
            (HttpMethod.Post, $"/api/v1/egg-grades/{id}/deactivate"),
            (HttpMethod.Post, $"/api/v1/egg-grades/{id}/activate"),
            (HttpMethod.Post, "/api/v1/inventory/items"),
            (HttpMethod.Put, $"/api/v1/inventory/items/{id}"),
            (HttpMethod.Post, $"/api/v1/inventory/items/{id}/deactivate"),
            (HttpMethod.Post, $"/api/v1/inventory/items/{id}/activate"),
            (HttpMethod.Post, $"/api/v1/inventory/items/{id}/adjustments"),
            (HttpMethod.Post, $"/api/v1/sales/{id}/void"),
            (HttpMethod.Put, $"/api/v1/water-usage/{id}"),
            (HttpMethod.Post, $"/api/v1/daily-entries/{id}/adjust"),
            (HttpMethod.Post, $"/api/v1/daily-entries/{id}/void"),
            (HttpMethod.Post, "/api/v1/users"),
            (HttpMethod.Put, $"/api/v1/users/{id}"),
            (HttpMethod.Put, $"/api/v1/users/{id}/password"),
            (HttpMethod.Post, "/api/v1/expense-categories"),
            (HttpMethod.Put, $"/api/v1/expense-categories/{id}"),
            (HttpMethod.Post, "/api/v1/expenses"),
            (HttpMethod.Put, $"/api/v1/expenses/{id}"),
            (HttpMethod.Post, $"/api/v1/sales/{id}/payments"),
            (HttpMethod.Post, $"/api/v1/payments/{id}/void"),
            (HttpMethod.Put, "/api/v1/account/settings"),
        ];

        foreach (var (method, url) in gated)
        {
            var response = await SendWithKeyAsync(worker, method, url, new { });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // GET /users is part of the admin-only group too, and the 403 carries a
        // problem body naming the missing role, not an empty response.
        var listUsers = await worker.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Forbidden, listUsers.StatusCode);
        Assert.Contains("Admin role", await listUsers.Content.ReadAsStringAsync());

        // Money data: READS are gated too (#87/#89), unlike the production
        // screens where workers browse freely.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await worker.GetAsync("/api/v1/expenses")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await worker.GetAsync("/api/v1/expense-categories")).StatusCode);

        // Farm settings (#123): the settings SCREEN is admin-only, but the
        // plain account read stays open — §4.5 formatting applies to every
        // screen a worker sees.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await worker.GetAsync("/api/v1/account/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await worker.GetAsync("/api/v1/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await worker.GetAsync($"/api/v1/sales/{id}/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await worker.GetAsync("/api/v1/customers/balances")).StatusCode);
    }

    [Fact]
    public async Task Worker_RunsTheFullDailyLoop()
    {
        var (admin, worker, accountId, farmId, flockId) = await SetupAsync();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var productId = await factory.SeedProductAsync(accountId, farmId, grades["Large"], "Large Eggs");

        // Entry → submit (creates the day's egg lots).
        // #394: submit requires exact reconciliation — total matches the grade.
        var entry = await worker.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId,
            houseId = Guid.NewGuid(),
            flockId,
            date = Today,
            totalEggs = 100,
            crackedEggs = 0,
            dirtyEggs = 0,
            discardedEggs = 0,
            mortalityCount = 0,
            grades = new[] { new { eggGradeId = grades["Large"], quantity = 100 } }
        });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        var entryId = (await entry.Content.ReadFromJsonAsync<Created>())!.Id;
        var submit = await worker.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        // Customer → draft order → line → confirm (FIFO allocation).
        var customer = await worker.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Worker's customer", phone = "555-0101" });
        Assert.Equal(HttpStatusCode.Created, customer.StatusCode);
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await worker.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        Assert.Equal(HttpStatusCode.Created, order.StatusCode);
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;
        var line = await worker.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 30, unitPriceMinorUnits = 100 });
        Assert.True(line.IsSuccessStatusCode);
        var confirm = await worker.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        // Feed: the item catalog is admin work, but receiving and using stock
        // is the worker's.
        var item = await admin.PostWithKeyAsync("/api/v1/inventory/items", Guid.NewGuid().ToString(),
            new { name = $"Layer mash {Guid.NewGuid():N}"[..30], category = "Feed", unit = "kg" });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        var itemId = (await item.Content.ReadFromJsonAsync<Created>())!.Id;
        var purchase = await worker.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/purchases", Guid.NewGuid().ToString(),
            new { receivedDate = Today, quantity = 50m, unitCostMinorUnits = 1000 });
        Assert.Equal(HttpStatusCode.Created, purchase.StatusCode);
        var usage = await worker.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 5m });
        Assert.Equal(HttpStatusCode.OK, usage.StatusCode);

        // Water and a new flock arriving are day's-work records too.
        var water = await worker.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 40m, source = "Well" });
        Assert.Equal(HttpStatusCode.Created, water.StatusCode);
        var newFlock = await worker.PostWithKeyAsync("/api/v1/flocks", Guid.NewGuid().ToString(),
            new { name = $"Arrivals {Guid.NewGuid():N}"[..20], breed = "ISA Brown", placementDate = Today, initialCount = 50 });
        Assert.Equal(HttpStatusCode.Created, newFlock.StatusCode);
    }

    [Fact]
    public async Task AccessToken_CarriesRoleClaim_OnlyForAdmins()
    {
        var adminEmail = $"admin-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(adminEmail);
        var workerEmail = $"worker-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);

        Assert.Equal("Admin", RoleClaim(await factory.LoginForAccessTokenAsync(adminEmail)));
        Assert.Null(RoleClaim(await factory.LoginForAccessTokenAsync(workerEmail)));
    }

    // The server re-reads roles at every refresh, so a demotion reaches
    // enforcement within one access-token lifetime — not at next login.
    [Fact]
    public async Task Refresh_ReReadsRoles_DemotionTakesEffect()
    {
        var adminEmail = $"admin-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(adminEmail);
        var flockId = await factory.SeedFlockAsync(accountId, Guid.NewGuid());
        var pair = await factory.LoginAsync(adminEmail);
        Assert.Equal("Admin", RoleClaim(pair.AccessToken));

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync(adminEmail);
            Assert.True((await users.RemoveFromRoleAsync(user!, "Admin")).Succeeded);
        }

        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        var refreshed = await client.PostRefreshAsync(pair.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var newPair = await TestHarness.ReadTokensAsync(refreshed);

        Assert.Null(RoleClaim(newPair.AccessToken));
        var demoted = factory.CreateAuthedClient(newPair.AccessToken);
        var gated = await SendWithKeyAsync(demoted, HttpMethod.Post, $"/api/v1/flocks/{flockId}/deplete");
        Assert.Equal(HttpStatusCode.Forbidden, gated.StatusCode);
    }

    // The idempotency replay path returns cached responses without invoking
    // the endpoint — authorization must run first so a worker replaying an
    // admin's key still gets 403, not the cached 201.
    [Fact]
    public async Task IdempotencyReplay_DoesNotBypassAdminGate()
    {
        var (admin, worker, _, _, _) = await SetupAsync();
        var key = Guid.NewGuid().ToString();

        var created = await admin.PostWithKeyAsync("/api/v1/users", key,
            new { email = $"replay-{Guid.NewGuid():N}@test.local", password = TestHarness.Password, role = "Worker" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var replayed = await worker.PostWithKeyAsync("/api/v1/users", key,
            new { email = $"replay-{Guid.NewGuid():N}@test.local", password = TestHarness.Password, role = "Worker" });
        Assert.Equal(HttpStatusCode.Forbidden, replayed.StatusCode);
    }

    // A duplicate email in ANOTHER tenant gets the generic message — the
    // admin endpoint must not double as a cross-tenant registration oracle.
    [Fact]
    public async Task DuplicateEmail_InAnotherAccount_GetsGenericMessage()
    {
        var foreignEmail = $"foreign-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(foreignEmail);

        var (admin, _, _, _, _) = await SetupAsync();
        var response = await admin.PostWithKeyAsync("/api/v1/users", Guid.NewGuid().ToString(),
            new { email = foreignEmail, password = TestHarness.Password, role = "Worker" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Could not create the user.", body);
        Assert.DoesNotContain("already", body);
    }

    [Fact]
    public async Task Admin_CreatesUsers_RoleValidated_AndListed()
    {
        var (admin, _, _, _, flockId) = await SetupAsync();
        var newWorkerEmail = $"hand-{Guid.NewGuid():N}@test.local";

        var created = await admin.PostWithKeyAsync("/api/v1/users", Guid.NewGuid().ToString(),
            new { email = newWorkerEmail, password = TestHarness.Password, role = "Worker" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The created worker can log in and record, but not correct.
        var hand = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(newWorkerEmail));
        var record = await hand.PostWithKeyAsync("/api/v1/water-usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 10m, source = "Well" });
        Assert.Equal(HttpStatusCode.Created, record.StatusCode);
        var gated = await SendWithKeyAsync(hand, HttpMethod.Post, $"/api/v1/flocks/{flockId}/deplete");
        Assert.Equal(HttpStatusCode.Forbidden, gated.StatusCode);

        // An admin-created ADMIN gets the role and passes the gate. #308 —
        // creating another Owner is step-up-gated, so re-confirm the current
        // password first and attach the grant.
        var newAdminEmail = $"boss-{Guid.NewGuid():N}@test.local";
        var grant = await admin.PostAsJsonAsync(
            "/api/v1/auth/step-up", new { password = TestHarness.Password });
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        var stepUpToken = (await grant.Content.ReadFromJsonAsync<StepUpDto>())!.Token;
        var createAdminRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users")
        {
            Content = JsonContent.Create(new { email = newAdminEmail, password = TestHarness.Password, role = "Admin" }),
        };
        createAdminRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        createAdminRequest.Headers.Add("X-Cluckwork-Step-Up", stepUpToken);
        var createdAdmin = await admin.SendAsync(createAdminRequest);
        Assert.Equal(HttpStatusCode.Created, createdAdmin.StatusCode);
        var boss = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(newAdminEmail));
        var bossGated = await boss.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.OK, bossGated.StatusCode);

        // Unknown role → 400; own-account duplicate → 422 with the friendly message.
        var badRole = await admin.PostWithKeyAsync("/api/v1/users", Guid.NewGuid().ToString(),
            new { email = $"x-{Guid.NewGuid():N}@test.local", password = TestHarness.Password, role = "Boss" });
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);
        var duplicate = await admin.PostWithKeyAsync("/api/v1/users", Guid.NewGuid().ToString(),
            new { email = newWorkerEmail, password = TestHarness.Password, role = "Worker" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Contains("already exists", await duplicate.Content.ReadAsStringAsync());

        var listed = await admin.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.Contains(listed!, u => u.Email == newWorkerEmail && u.Role == "Worker");
        Assert.Contains(listed!, u => u.Email == newAdminEmail && u.Role == "Admin");
    }

    // Reads the "role" claim straight from the JWT payload — the same short
    // name the API validates (RoleClaimType) and the SPA decodes.
    private static string? RoleClaim(string accessToken)
    {
        var payload = accessToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return doc.RootElement.TryGetProperty("role", out var role) ? role.GetString() : null;
    }
}
