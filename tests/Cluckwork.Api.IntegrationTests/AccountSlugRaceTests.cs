namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #531 — Suspend()/Reactivate() each bump the account's Version concurrency
// token, so two writers racing the SAME row cannot both win: the loser's
// UPDATE ... WHERE "Version" = N matches zero rows and EF raises
// DbUpdateConcurrencyException instead of silently overwriting. Version-loss
// has shipped three times in this repo, so every new aggregate mutation gets a
// real parallel-race test. Same held-open-snapshot shape as FarmBannerTests
// (two contexts, not two HTTP calls — the test host would serialise those).
[Collection(IntegrationCollection.Name)]
public sealed class AccountSlugRaceTests(CluckworkWebApplicationFactory factory)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}@test.local";

    [Fact]
    public async Task TwoConcurrentSuspends_TheLoserGetsAConcurrencyConflict()
    {
        var accountId = await factory.SeedAccountWithUserAsync(Unique("suspend"));

        var conflict = await Record.ExceptionAsync(() =>
            factory.WithTenantScopeAsync(accountId, dbA =>
                factory.WithTenantScopeAsync(accountId, async dbB =>
                {
                    var a = await dbA.Accounts.FirstAsync();
                    var b = await dbB.Accounts.FirstAsync();

                    a.Suspend();
                    await dbA.SaveChangesAsync();

                    b.Suspend();
                    await dbB.SaveChangesAsync();
                })));

        Assert.IsType<DbUpdateConcurrencyException>(conflict);
    }

    [Fact]
    public async Task TwoConcurrentReactivates_TheLoserGetsAConcurrencyConflict()
    {
        var accountId = await factory.SeedAccountWithUserAsync(Unique("reactivate"));
        // Start suspended so both writers perform a real Reactivate.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var account = await db.Accounts.FirstAsync();
            account.Suspend();
            await db.SaveChangesAsync();
        });

        var conflict = await Record.ExceptionAsync(() =>
            factory.WithTenantScopeAsync(accountId, dbA =>
                factory.WithTenantScopeAsync(accountId, async dbB =>
                {
                    var a = await dbA.Accounts.FirstAsync();
                    var b = await dbB.Accounts.FirstAsync();

                    a.Reactivate();
                    await dbA.SaveChangesAsync();

                    b.Reactivate();
                    await dbB.SaveChangesAsync();
                })));

        Assert.IsType<DbUpdateConcurrencyException>(conflict);
    }
}
