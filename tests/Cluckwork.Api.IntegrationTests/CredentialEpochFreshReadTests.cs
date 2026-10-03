using System.Data.Common;
using System.Net;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cluckwork.Api.IntegrationTests;

// #857 — the per-request credential read (#364) must stay a fresh database
// round trip. The existing suites change credentials through the application's
// own services or a tracked SaveChanges, and none of them uses the bearer before
// the change, so a cache that the application clears on its own writes, or one
// that a cold first request leaves empty, passes all of them. Every test here
// uses the bearer first, then changes the row over an independent connection
// the application never sees, and counts the read on every request.
//
// The host clock is frozen so that a cache expiring on TimeProvider can only
// hit: with a running clock, five slow requests could each outlive its TTL.
public sealed class CredentialEpochFreshReadFactory : CluckworkWebApplicationFactory
{
    public CredentialReadInterceptor CredentialReads { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Database:Resilience:MaxRetryDelaySeconds", "1");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(new FrozenTimeProvider(DateTimeOffset.UtcNow));
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(CredentialReads));
        });
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

// Counts executions of the credential read: the one statement that selects a
// user's CredentialEpoch together with the account's IsActive. It can also fail
// that statement, transiently once or permanently.
public sealed class CredentialReadInterceptor : DbCommandInterceptor
{
    private int _count;
    private volatile Fault _fault;

    public enum Fault { None, TransientOnce, Permanent }

    public int Count => Volatile.Read(ref _count);

    public void Inject(Fault fault) => _fault = fault;

    public static bool IsCredentialRead(DbCommand command) =>
        command.CommandText.Contains("\"CredentialEpoch\"", StringComparison.Ordinal)
        && command.CommandText.Contains("\"IsActive\"", StringComparison.Ordinal)
        && command.CommandText.Contains("FROM \"AspNetUsers\"", StringComparison.Ordinal);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (IsCredentialRead(command))
        {
            Interlocked.Increment(ref _count);
            switch (_fault)
            {
                case Fault.TransientOnce:
                    _fault = Fault.None;
                    throw TransientFault.Create();
                case Fault.Permanent:
                    throw new InvalidOperationException("simulated non-transient failure of the credential read");
            }
        }
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

public sealed class CredentialEpochFreshReadTests(CredentialEpochFreshReadFactory factory)
    : IClassFixture<CredentialEpochFreshReadFactory>, IDisposable
{
    public void Dispose() => factory.CredentialReads.Inject(CredentialReadInterceptor.Fault.None);

    private sealed record ActiveBearer(Guid AccountId, Guid UserId, HttpClient Client, string RefreshToken);

    // Signs in and proves the bearer works, so any cache would now hold this user.
    private async Task<ActiveBearer> ActiveBearerAsync()
    {
        var email = $"fresh-read-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        var tokens = await factory.LoginAsync(email);
        var client = factory.CreateAuthedClient(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/users")).StatusCode);
        return new ActiveBearer(accountId, userId, client, tokens.RefreshToken);
    }

    private async Task ExecuteOnIndependentConnectionAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AssertRejectedAsync(HttpClient client, string expectedTitle)
    {
        var response = await client.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(expectedTitle, (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task RaisingTheEpochOnAnotherConnection_RejectsABearerAlreadyInUse()
    {
        var bearer = await ActiveBearerAsync();

        await ExecuteOnIndependentConnectionAsync(
            """UPDATE "AspNetUsers" SET "CredentialEpoch" = "CredentialEpoch" + 1 WHERE "Id" = @id""",
            bearer.UserId);

        await AssertRejectedAsync(bearer.Client, "Auth.CredentialsSuperseded");
    }

    [Fact]
    public async Task DisablingTheUserOnAnotherConnection_RejectsABearerAlreadyInUse()
    {
        var bearer = await ActiveBearerAsync();

        await ExecuteOnIndependentConnectionAsync(
            """UPDATE "AspNetUsers" SET "DisabledAt" = now() WHERE "Id" = @id""", bearer.UserId);

        await AssertRejectedAsync(bearer.Client, "Auth.AccountDisabled");
    }

    [Fact]
    public async Task DeactivatingTheFarmOnAnotherConnection_RejectsABearerAlreadyInUse()
    {
        var bearer = await ActiveBearerAsync();

        await ExecuteOnIndependentConnectionAsync(
            """UPDATE "Accounts" SET "IsActive" = false WHERE "Id" = @id""", bearer.AccountId);

        await AssertRejectedAsync(bearer.Client, AuthEndpoints.FarmSuspendedCode);
    }

    [Fact]
    public async Task DeletingTheUserOnAnotherConnection_RejectsABearerAlreadyInUse()
    {
        var bearer = await ActiveBearerAsync();

        await ExecuteOnIndependentConnectionAsync(
            """DELETE FROM "AspNetUsers" WHERE "Id" = @id""", bearer.UserId);

        await AssertRejectedAsync(bearer.Client, "Auth.CredentialsSuperseded");
    }

    // The exact count holds for fault-free requests only: #269 lets EF retry
    // this read after a transient fault, which the next test pins.
    [Fact]
    public async Task EveryAuthenticatedRequest_ReadsTheCredentialRowOnce_AndOnlyLogoutIsExempt()
    {
        var bearer = await ActiveBearerAsync();

        for (var request = 1; request <= 5; request++)
        {
            var before = factory.CredentialReads.Count;
            Assert.Equal(HttpStatusCode.OK, (await bearer.Client.GetAsync("/api/v1/users")).StatusCode);
            Assert.Equal(1, factory.CredentialReads.Count - before);
        }

        var beforeDescendant = factory.CredentialReads.Count;
        await bearer.Client.SendAsync(LogoutRequest("/api/v1/auth/logout/anything", bearer));
        Assert.Equal(1, factory.CredentialReads.Count - beforeDescendant);

        foreach (var path in new[] { "/api/v1/auth/logout/", "/api/v1/auth/logout" })
        {
            var beforeLogout = factory.CredentialReads.Count;
            Assert.Equal(HttpStatusCode.NoContent,
                (await bearer.Client.SendAsync(LogoutRequest(path, bearer))).StatusCode);
            Assert.Equal(0, factory.CredentialReads.Count - beforeLogout);
        }
    }

    [Fact]
    public async Task ATransientFaultOnTheCredentialRead_IsRetriedOnce_AndTheRequestProceeds()
    {
        var bearer = await ActiveBearerAsync();
        factory.CredentialReads.Inject(CredentialReadInterceptor.Fault.TransientOnce);

        var before = factory.CredentialReads.Count;
        var response = await bearer.Client.GetAsync("/api/v1/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, factory.CredentialReads.Count - before);
    }

    [Fact]
    public async Task AFailedCredentialRead_NeverAdmitsTheRequest()
    {
        var bearer = await ActiveBearerAsync();
        var name = $"must-not-exist-{Guid.NewGuid():N}";
        factory.CredentialReads.Inject(CredentialReadInterceptor.Fault.Permanent);

        var response = await bearer.Client.PostWithKeyAsync(
            "/api/v1/expense-categories", Guid.NewGuid().ToString(), new { name });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(await factory.WithTenantScopeAsync(bearer.AccountId, db =>
            db.ExpenseCategories.AnyAsync(c => c.Name == name)));
    }

    private static HttpRequestMessage LogoutRequest(string path, ActiveBearer bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add("Cookie", $"{AuthCookies.RefreshCookieNameFor(bearer.AccountId)}={bearer.RefreshToken}");
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, bearer.AccountId.ToString());
        return request;
    }
}
