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
    public static async Task<Guid> SeedAccountWithUserAsync(
        this CluckworkWebApplicationFactory factory, string email)
    {
        var accountId = Guid.NewGuid();

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Accounts.Add(Account.Create(accountId, "Test Farm Co", "UTC", "USD"));
            await db.SaveChangesAsync();
        });

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

        return accountId;
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

    // Seeds an egg lot for the account. Pass restrictedUntil to make it withdrawal-restricted.
    public static async Task<Guid> SeedEggLotAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId,
        string gradeCode, int quantity, DateOnly? restrictedUntil = null)
    {
        var lotId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var lot = EggLot.Create(lotId, accountId, Guid.NewGuid(),
                DateOnly.FromDateTime(DateTime.UtcNow.Date), gradeCode, quantity);
            if (restrictedUntil is not null)
                lot.SetWithdrawalRestriction(restrictedUntil.Value);
            db.EggLots.Add(lot);
            await db.SaveChangesAsync();
        });
        return lotId;
    }

    // Seeds a draft sales order with a single line item for the given grade/quantity.
    public static async Task<Guid> SeedSalesOrderAsync(
        this CluckworkWebApplicationFactory factory, Guid accountId,
        string gradeCode, int quantity)
    {
        var orderId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var order = SalesOrder.Create(
                orderId, accountId, Guid.NewGuid(),
                $"SO-{orderId.ToString()[..8]}", DateOnly.FromDateTime(DateTime.UtcNow.Date), "USD");
            order.AddItem(Guid.NewGuid(), gradeCode, quantity, Cluckwork.Domain.Common.Money.Zero("USD"));
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
