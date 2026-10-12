using Cluckwork.Application.Modules.Farm.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cluckwork.Infrastructure.Persistence;

public sealed partial class SimulationDataSeeder
{
    // --- Primary account timezone (BEFORE any dated data exists) -------

    private async Task SeedPrimaryTimeZoneAsync(SimCast cast, SimulationOptions sim, CancellationToken ct)
    {
        // Farm settings are the Owner's to change (#500) — Account.UpdateSettings
        // is audited. This ActAs is load-bearing, not decorative: the phase now
        // runs directly after a MANAGER-authored one, so dropping it misattributes
        // the row instead of doing nothing.
        ActAs(cast.Owner);

        var account = await farm.GetSettingsAsync(ct)
            ?? throw new InvalidOperationException("Simulation seed: the primary account was not found.");

        if (string.Equals(account.TimeZoneId, sim.TimeZoneId, StringComparison.Ordinal))
            return; // already set — idempotent re-run.

        var command = new UpdateFarmSettingsCommand(
            account.Name,
            sim.TimeZoneId,
            account.Locale,
            account.CurrencyCode,
            account.UnitSystem.ToString(),
            account.FirstDayOfWeek?.ToString(),
            account.DateFormatOverride,
            account.TimeFormatOverride,
            account.Brand,
            account.DefaultStepperUnit.ToString(),
            account.WorkerSaleAllocationPolicy.ToString(),
            account.Version,
            // The command replaces the WHOLE settings block, so this phase must
            // carry the farm's current ceiling forward or it would silently
            // clear one (#727). The fixture sets none today; that is not a
            // reason to hard-code null here.
            account.MaxDiscountPercent);

        var result = await farm.UpdateSettingsAsync(command, ct);
        Require(result, $"set primary account timezone to {sim.TimeZoneId}");
    }

    // --- Second, pristine account (tenant-isolation fixture) -----------

    private async Task SeedSecondAccountAsync(CancellationToken ct)
    {
        // The account query filter would hide a second tenant's row — the
        // fixture reads with it off, regardless of which tenant is resolved.
        if (await farmFixture.AccountExistsAsync(SecondAccountId, ct)) return;

        // #546 — this row belongs to the SECOND account while this seeder's own
        // scope is resolved to the PRIMARY one (see SeedAsync), so writing it
        // through the seeder's own scope is a cross-tenant write and TenantStampInterceptor now
        // refuses it. Give the write its own scope resolved to the account it
        // actually writes, mirroring DailyEntryLockSweep.LockDueEntriesAsync.
        //
        // BOTH the TenantContext and the fixture must come from the new scope.
        // The interceptor is bound to the scope that constructed its
        // DbContext, so resolving the tenant here and still writing through the
        // injected farmFixture would leave the guard reading the PRIMARY
        // account — a no-op that looks like a fix. Resolving the injected
        // `tenant` field instead would throw: it is single-assignment as of
        // this same slice.
        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(SecondAccountId);
        await scope.ServiceProvider.GetRequiredService<IFarmFixture>().CreateAccountAsync(
            SecondAccountId, "Simulation Second Farm", "simulation-second-farm", "UTC", "USD", ct);
        logger.LogInformation("Seeded second pristine simulation account {AccountId}.", SecondAccountId);
    }
}
