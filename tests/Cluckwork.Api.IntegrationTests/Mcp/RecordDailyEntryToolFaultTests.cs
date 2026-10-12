using System.Data.Common;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Cluckwork.Api.IntegrationTests.Mcp.McpConnection;
using static Cluckwork.Api.IntegrationTests.Mcp.RecordDailyEntryToolTests;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #809 — the #307 suites cover these paths over HTTP only; the tool reaches the protocol
// through its own adapter, so the stolen lease and the lost commit acknowledgment are
// proven again here.
public sealed class McpIdempotencyFaultFactory : CluckworkWebApplicationFactory
{
    public TransientCommitFaultInterceptor CommitFault { get; } = new();

    public LeaseThiefInterceptor Thief { get; } = new();

    public ClaimCounter Claims { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Idempotency:MaxWaitSeconds", "1");
        builder.ConfigureTestServices(services =>
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(CommitFault, Thief, Claims)));
    }
}

public sealed class RecordDailyEntryToolFaultTests(McpIdempotencyFaultFactory factory)
    : IClassFixture<McpIdempotencyFaultFactory>, IDisposable
{
    public void Dispose()
    {
        factory.CommitFault.Disarm();
        factory.Thief.Disarm();
    }

    // Another attempt takes the lease while the tool's write is in flight. The tool's
    // publish then misses, so its write rolls back, and the key recovers once the thief's
    // lease lapses.
    [Fact]
    public async Task StolenLease_RollsTheWriteBack_AndTheKeyRecovers()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(factory, Roles.Manager);
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, [OAuthScopes.WriteDailyEntries]));
        var arguments = Arguments(farm.FlockName);

        factory.Thief.ArmOnce(factory.ConnectionString, farm.User.AccountId);
        var lost = await CallAsync(client, arguments);
        var entriesAfterLoss = await EntryCountAsync(factory, farm);
        var auditAfterLoss = await factory.WithTenantScopeAsync(farm.User.AccountId,
            db => db.AuditEvents.CountAsync(a => a.Action == AuditActions.DailyEntryCreate));
        var recovered = await RecordAsync(client, arguments);

        Assert.Equal(1, factory.Thief.Steals);
        Assert.Contains("Idempotency.InProgress:", ErrorText(lost));
        Assert.Equal(0, entriesAfterLoss);
        Assert.Equal(0, auditAfterLoss);
        Assert.False(recovered.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, await EntryCountAsync(factory, farm));
    }

    // An unknown flock is refused inside the claim; a missing one before it.
    [Fact]
    public async Task NullFlock_IsRefusedBeforeTheKeyIsClaimed()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(factory, Roles.Manager);
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, [OAuthScopes.WriteDailyEntries]));

        factory.Claims.Reset();
        var missing = await CallAsync(client, With(Arguments(farm.FlockName), "flock", null));
        var claimsForMissing = factory.Claims.Count;
        var unknown = await CallAsync(client, Arguments("No Such Flock"));

        Assert.EndsWith(": flock is required.", ErrorText(missing));
        Assert.Equal(0, claimsForMissing);
        Assert.EndsWith("No flock you can see is named or numbered 'No Such Flock'.", ErrorText(unknown));
        Assert.Equal(1, factory.Claims.Count);
    }

    // Postgres committed the entry and the claim's publication together, and only the
    // acknowledgment was lost. The tool answers with what it published, once.
    [Fact]
    public async Task LostCommitAcknowledgment_ReturnsThePublishedEntry_WrittenOnce()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(factory, Roles.Manager);
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, [OAuthScopes.WriteDailyEntries]));

        factory.CommitFault.ArmOnce();
        var reply = await RecordAsync(client, Arguments(farm.FlockName));

        Assert.True(factory.CommitFault.Commits >= 1, "no commit ran while the fault was armed");
        Assert.False(reply.GetProperty("replayed").GetBoolean());
        Assert.Equal([AuditActions.DailyEntryCreate],
            (await AuditAsync(factory, farm.User.AccountId, reply.GetProperty("dailyEntryId").GetGuid())).Select(a => a.Action));
    }
}

// Counts idempotency claim attempts.
public sealed class ClaimCounter : DbCommandInterceptor
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("INSERT INTO idempotency_records", StringComparison.Ordinal))
            Interlocked.Increment(ref _count);
        return ValueTask.FromResult(result);
    }
}

// Steals the account's in-progress claim from a separate connection the moment the
// daily-entry INSERT runs, and backdates it so a later retry may take it back.
public sealed class LeaseThiefInterceptor : DbCommandInterceptor
{
    private int _armed;
    private int _steals;
    private string _connectionString = string.Empty;
    private Guid _accountId;

    public int Steals => Volatile.Read(ref _steals);

    public void ArmOnce(string connectionString, Guid accountId)
    {
        _connectionString = connectionString;
        _accountId = accountId;
        Interlocked.Exchange(ref _steals, 0);
        Interlocked.Exchange(ref _armed, 1);
    }

    public void Disarm() => Interlocked.Exchange(ref _armed, 0);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await StealAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await StealAsync(command, cancellationToken);
        return result;
    }

    private async Task StealAsync(DbCommand command, CancellationToken ct)
    {
        if (!command.CommandText.Contains("INSERT INTO \"DailyEntries\"", StringComparison.Ordinal)
            || Interlocked.CompareExchange(ref _armed, 0, 1) != 1)
            return;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var steal = new NpgsqlCommand(
            """
            UPDATE idempotency_records SET "LeaseOwner" = gen_random_uuid(), "LeaseExpiresAt" = now() - interval '1 minute'
            WHERE "AccountId" = @accountId AND "Status" = 0
            """, connection);
        steal.Parameters.AddWithValue("accountId", _accountId);
        Interlocked.Add(ref _steals, await steal.ExecuteNonQueryAsync(ct));
    }
}
