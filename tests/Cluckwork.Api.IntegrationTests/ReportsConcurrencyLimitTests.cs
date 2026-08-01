namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Reports;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

// #311 — end-to-end proof that ReportConcurrencyLimitFilter is actually wired
// onto the reports route group (ReportConcurrencyLimiterTests covers the
// limiter's own acquire/reject/release logic in isolation). PermitLimit is
// pinned to 1 here so a single held request saturates the account's bucket,
// and IReportQueries is swapped for a gated decorator so the test can hold
// that request in flight deterministically — no sleep-based timing race.
public sealed class ReportsConcurrencyLimitFactory : CluckworkWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:ReportsConcurrency:PermitLimit", "1");
        builder.UseSetting("RateLimiting:ReportsConcurrency:QueueLimit", "0");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ReportGate>();
            services.AddScoped<IReportQueries>(sp => new GateReportQueries(
                new ReportQueries(sp.GetRequiredService<AppDbContext>()),
                sp.GetRequiredService<TenantContext>(),
                sp.GetRequiredService<ReportGate>()));
        });
    }
}

// Blocks GetProductionAsync mid-flight for whichever single account is
// currently "armed", so a test can hold a concurrency permit open on demand.
// A call for any OTHER account passes straight through unblocked.
public sealed class ReportGate
{
    private Guid? _armedAccountId;
    private TaskCompletionSource? _gate;
    private int _admittedCount;

    public int AdmittedCount => Volatile.Read(ref _admittedCount);

    public void Arm(Guid accountId)
    {
        _armedAccountId = accountId;
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _admittedCount, 0);
    }

    public void Release() => _gate?.TrySetResult();

    public Task WaitIfArmedForAsync(Guid accountId, CancellationToken ct)
    {
        if (_gate is null || _armedAccountId != accountId)
            return Task.CompletedTask;
        Interlocked.Increment(ref _admittedCount);
        return _gate.Task.WaitAsync(ct);
    }
}

public sealed class GateReportQueries(
    IReportQueries inner, TenantContext tenant, ReportGate gate) : IReportQueries
{
    public async Task<ProductionReport> GetProductionAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await gate.WaitIfArmedForAsync(tenant.AccountId, ct);
        return await inner.GetProductionAsync(from, to, ct);
    }

    public Task<SalesSummary> GetSalesAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        inner.GetSalesAsync(from, to, ct);

    public Task<ExpenseSummary> GetExpensesAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        inner.GetExpensesAsync(from, to, ct);

    public Task<ProfitReport> GetProfitAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        inner.GetProfitAsync(from, to, ct);
}

public sealed class ReportsConcurrencyLimitTests(ReportsConcurrencyLimitFactory factory)
    : IClassFixture<ReportsConcurrencyLimitFactory>
{
    [Fact]
    public async Task Concurrent_report_over_the_cap_429s_without_affecting_another_account()
    {
        var emailA = $"a-{Guid.NewGuid():N}@test.local";
        var accountA = await factory.SeedAccountWithUserAsync(emailA);
        var clientA = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(emailA));

        var emailB = $"b-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(emailB);
        var clientB = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(emailB));

        var gate = factory.Services.GetRequiredService<ReportGate>();
        gate.Arm(accountA);

        var held = clientA.GetAsync("/api/v1/reports/production");
        await WaitUntilAsync(() => gate.AdmittedCount >= 1);

        // Account A is already at its permit limit (1) — a second concurrent
        // request for A must be rejected, not queued.
        var rejected = await clientA.GetAsync("/api/v1/reports/production");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"),
            "429 must carry a Retry-After header");

        // Account B has its own bucket — unaffected while A is saturated.
        var okForB = await clientB.GetAsync("/api/v1/reports/production");
        Assert.Equal(HttpStatusCode.OK, okForB.StatusCode);

        gate.Release();
        var completed = await held;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Condition was never met.");
    }
}
