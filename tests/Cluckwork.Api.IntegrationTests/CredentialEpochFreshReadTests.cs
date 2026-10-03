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
// round trip. CredentialEpochTests changes the epoch through the application's
// services or a tracked SaveChanges, without using the bearer first, so a cache
// the application clears on its own writes passes it. Only AccountSuspensionTests'
// inactive-farm case uses its bearer before changing the row. These tests cover
// the rest: the revocation tests use the bearer, then change the epoch, the
// disabled flag, the farm or the user over an independent connection the
// application never sees; the count test counts the read on every request; the
// overlap test holds one read in flight while a second request arrives.
//
// The host clock's GetUtcNow is frozen, so a cache that expires on
// TimeProvider.GetUtcNow can only hit. GetTimestamp and CreateTimer are not
// frozen, and a cache expiring on either could still expire mid-test.
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
// that statement (transiently once, permanently, or as a cancellation), or hold
// the next one after it has executed.
public sealed class CredentialReadInterceptor : DbCommandInterceptor
{
    private int _count;
    private volatile Fault _fault;
    private TaskCompletionSource? _pauseEntered;
    private TaskCompletionSource? _pauseRelease;

    public enum Fault { None, TransientOnce, Permanent, Cancelled }

    public int Count => Volatile.Read(ref _count);

    public void Inject(Fault fault) => _fault = fault;

    // Holds the next credential read once it has executed, so the row state it
    // returns predates anything the test commits while it is held.
    public (Task Entered, Action Release) PauseNextReadAfterExecution()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pauseRelease = release;
        Volatile.Write(ref _pauseEntered, entered);
        return (entered.Task, () => release.TrySetResult());
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        if (IsCredentialRead(command) && Interlocked.Exchange(ref _pauseEntered, null) is { } entered)
        {
            entered.TrySetResult();
            await _pauseRelease!.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

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
                case Fault.Cancelled:
                    throw new OperationCanceledException("simulated cancellation of the credential read");
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

    // A cancelled read is a failed read, so it surfaces as an error. Turning it
    // into any verdict breaks the port's contract, and while Current was the
    // enum's zero value, `return default` admitted the request.
    [Fact]
    public async Task ACancelledCredentialRead_IsAnErrorNotAVerdict()
    {
        var bearer = await ActiveBearerAsync();
        var name = $"must-not-exist-{Guid.NewGuid():N}";
        factory.CredentialReads.Inject(CredentialReadInterceptor.Fault.Cancelled);

        var response = await bearer.Client.PostWithKeyAsync(
            "/api/v1/expense-categories", Guid.NewGuid().ToString(), new { name });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(await factory.WithTenantScopeAsync(bearer.AccountId, db =>
            db.ExpenseCategories.AnyAsync(c => c.Name == name)));
    }

    // The first request's read is held after it has executed, so its result
    // predates the revocation committed next. A second request with the same
    // bearer must run its own read and be refused at once. A Singleton verifier
    // (one captured AppDbContext) or a cache of in-flight reads fails this; the
    // sequential tests above see neither.
    [Fact]
    public async Task AnOverlappingRequest_ReadsItsOwnCredential_WhileAnEarlierReadIsInFlight()
    {
        var bearer = await ActiveBearerAsync();
        var (entered, release) = factory.CredentialReads.PauseNextReadAfterExecution();
        var first = bearer.Client.GetAsync("/api/v1/users");
        try
        {
            await entered.WaitAsync(TimeSpan.FromSeconds(30));
            await ExecuteOnIndependentConnectionAsync(
                """UPDATE "AspNetUsers" SET "CredentialEpoch" = "CredentialEpoch" + 1 WHERE "Id" = @id""",
                bearer.UserId);
            var readsBefore = factory.CredentialReads.Count;

            var second = bearer.Client.GetAsync("/api/v1/users");
            await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(second.IsCompleted, "the second request waited on the first request's credential read");
            var response = await second;
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Auth.CredentialsSuperseded",
                (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
            Assert.Equal(1, factory.CredentialReads.Count - readsBefore);
        }
        finally
        {
            release();
        }

        // Its read completed before the revocation, so it is admitted: the
        // accepted in-flight window, and proof the hold came after execution.
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
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
