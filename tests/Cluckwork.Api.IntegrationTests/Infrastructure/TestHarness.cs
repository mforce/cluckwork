namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using System.Net.Http.Headers;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

// Seeding + auth helpers shared by the integration tests. Each helper that touches the
// database opens its own DI scope with a resolved TenantContext, mirroring how the
// tenant middleware resolves the account per request in production.
internal static class TestHarness
{
    public const string Password = "TestPassw0rd!23";

    // Creates an Account row + an ApplicationUser bound to it, returning the account id.
    // Users are Admin by default — #73 gated the corrective endpoints, and most tests
    // exercise them; pass asAdmin: false for a worker.
    public static async Task<Guid> SeedAccountWithUserAsync(
        this CluckworkWebApplicationFactory factory, string email, bool asAdmin = true)
    {
        var accountId = Guid.NewGuid();

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Accounts.Add(Account.Create(accountId, "Test Farm Co", "UTC", "USD"));
            // Every account carries the packed-unit defaults (#97) — mirrors the
            // startup seeder's SeedDefaultEggUnitConversionsAsync.
            db.EggUnitConversions.AddRange(
                Cluckwork.Domain.Catalog.EggUnitConversion.Defaults(accountId));
            await db.SaveChangesAsync();
        });

        await factory.SeedUserAsync(accountId, email, asAdmin);
        return accountId;
    }

    // Adds another user to an existing account (e.g. a worker beside the admin).
    public static async Task SeedUserAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId, string email, bool asAdmin)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            AccountId = accountId
        };
        var result = await users.CreateAsync(user, Password);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "Seed user creation failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));

        if (asAdmin)
        {
            // The startup seeder doesn't run in tests (Seed:Enabled unset) — create
            // the role on first use. Tests in the collection run sequentially, so
            // the exists-then-create pair doesn't race.
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roles.RoleExistsAsync(DatabaseSeeder.AdminRole))
                await roles.CreateAsync(new ApplicationRole { Id = Guid.NewGuid(), Name = DatabaseSeeder.AdminRole });

            var added = await users.AddToRoleAsync(user, DatabaseSeeder.AdminRole);
            if (!added.Succeeded)
                throw new InvalidOperationException(
                    "Seed role assignment failed: " + string.Join("; ", added.Errors.Select(e => e.Description)));
        }
    }

    // Opens a scope, resolves the tenant to accountId, and hands the AppDbContext to the
    // caller. The query filter and tenant-stamp interceptor then behave exactly as in a
    // real request for that account.
    public static async Task WithTenantScopeAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId, Func<AppDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.Resolve(accountId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await action(db);
    }

    public static async Task<T> WithTenantScopeAsync<T>(
        this CluckworkWebApplicationFactory factory, Guid accountId, Func<AppDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.Resolve(accountId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await action(db);
    }

    // Seeds saleable egg grades for the account + farm; returns name -> id.
    // Grades are farm-scoped (spec §9.1) — pass the same farmId the test posts.
    public static async Task<Dictionary<string, Guid>> SeedEggGradesAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId, Guid farmId, params string[] names)
    {
        var ids = new Dictionary<string, Guid>();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var sort = 0;
            foreach (var name in names)
            {
                var grade = EggGrade.Create(
                    Guid.NewGuid(), accountId, farmId,
                    name, EggGradeType.Size, sort++, isSaleable: true);
                db.EggGrades.Add(grade);
                ids[name] = grade.Id;
            }
            await db.SaveChangesAsync();
        });
        return ids;
    }

    // Seeds an Active flock — daily entries now require a live flock row (#47),
    // so tests can no longer post entries against invented flock ids.
    public static async Task<Guid> SeedFlockAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId, Guid farmId, Guid? houseId = null)
    {
        var flockId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Flocks.Add(Cluckwork.Domain.Flocks.Flock.Create(
                flockId, accountId, farmId, houseId ?? Guid.NewGuid(),
                $"Flock-{flockId.ToString()[..8]}", "Test Breed",
                DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(-30), initialCount: 100));
            await db.SaveChangesAsync();
        });
        return flockId;
    }

    // Seeds an egg lot for the account. Pass restrictedUntil to make it withdrawal-restricted.
    // eggGradeId must reference a seeded EggGrade row (see SeedEggGradesAsync) — lots FK to grades.
    public static async Task<Guid> SeedEggLotAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId,
        Guid eggGradeId, int quantity, DateOnly? restrictedUntil = null,
        DateOnly? productionDate = null)
    {
        var lotId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var lot = EggLot.Create(lotId, accountId, Guid.NewGuid(),
                productionDate ?? DateOnly.FromDateTime(DateTime.UtcNow.Date), eggGradeId, quantity);
            if (restrictedUntil is not null)
                lot.SetWithdrawalRestriction(restrictedUntil.Value);
            db.EggLots.Add(lot);
            await db.SaveChangesAsync();
        });
        return lotId;
    }

    // Seeds a draft sales order with a single line item for the given grade/quantity.
    public static Task<Guid> SeedSalesOrderAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId,
        Guid eggGradeId, int quantity) =>
        factory.SeedSalesOrderAsync(accountId, [(eggGradeId, quantity)]);

    // Multi-line variant (one line per grade/quantity pair).
    public static async Task<Guid> SeedSalesOrderAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId,
        IReadOnlyList<(Guid EggGradeId, int Quantity)> lines)
    {
        var orderId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            // Orders FK to Customers — seed a real customer row.
            var customer = Customer.Create(Guid.NewGuid(), accountId, "Seed Customer", "555-0000");
            db.Customers.Add(customer);

            var order = SalesOrder.Create(
                orderId, accountId, customer.Id,
                $"SO-{orderId.ToString()[..8]}", DateOnly.FromDateTime(DateTime.UtcNow.Date), "USD");
            foreach (var (eggGradeId, quantity) in lines)
                order.AddItem(eggGradeId, quantity, Cluckwork.Domain.Common.Money.Zero("USD"));
            db.SalesOrders.Add(order);
            await db.SaveChangesAsync();
        });
        return orderId;
    }

    // Logs in over HTTP and returns the full token pair.
    public static async Task<TokenPairDto> LoginAsync(
        this CluckworkWebApplicationFactory factory, string email)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { email, password = Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenPairDto>())!;
    }

    public static async Task<string> LoginForAccessTokenAsync(
        this CluckworkWebApplicationFactory factory, string email) =>
        (await factory.LoginAsync(email)).AccessToken;

    public static HttpClient CreateAuthedClient(
        this CluckworkWebApplicationFactory factory, string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    // POST with the Idempotency-Key write endpoints require. Optional JSON body.
    public static Task<HttpResponseMessage> PostWithKeyAsync(
        this HttpClient client, string url, string idempotencyKey, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }
}

public sealed record TokenPairDto(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiry);
