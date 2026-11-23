using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.IntegrationTests;

// #1284 r1 item 1: registration inserts and never updates. A coworker's (or
// another farm's user's) finish with the same credential id lands after the
// attacker's duplicate check passed and before the attacker's write; the
// attacker is refused, nothing of the coworker's row changes, and the
// attacker's ceremony stays spent. The pause is the attacker's second command
// on the passkey table, whatever that command is.
public sealed partial class SecondFactorLoginTests
{
    private async Task AssertADuplicatePasskeyRaceIsRefusedAsync(bool crossFarm)
    {
        var (_, accountId, attackerId, attacker, _) = await SignedInPlainAsync(asOwner: false);
        var victimEmail = $"pk-victim-{Guid.NewGuid():N}@test.local";
        var victimAccount = crossFarm ? await factory.SeedAccountWithUserAsync(victimEmail, asAdmin: false) : accountId;
        if (!crossFarm) await factory.SeedUserAsync(accountId, victimEmail, Roles.Manager);
        var victimId = await UserIdAsync(victimAccount, victimEmail);
        var victim = factory.CreateAuthedClient((await factory.LoginAsync(victimEmail)).AccessToken);
        var attackerKey = new SoftwareAuthenticator();
        var victimKey = new SoftwareAuthenticator(attackerKey.CredentialId);
        var (attackerGrant, victimGrant) = (await GrantOf(await StepUpAsync(attacker, TestHarness.Password)),
            await GrantOf(await StepUpAsync(victim, TestHarness.Password)));
        var (attackerCeremony, victimCeremony) = (await StartPasskeyAsync(attacker), await StartPasskeyAsync(victim));
        var commands = 0;
        factory.BeforeCommand.Arm("\"AspNetUserPasskeys\"", async () =>
        {
            if (Interlocked.Increment(ref commands) != 2) return;
            // The victim's first passkey: 200 with a fresh pair.
            Assert.Equal(HttpStatusCode.OK, (await FinishPasskeyAsync(victim, victimGrant, victimCeremony.Id,
                victimKey.Attest(victimCeremony.Options), "Victim's key")).StatusCode);
        }, times: 2);

        var response = await FinishPasskeyAsync(attacker, attackerGrant, attackerCeremony.Id,
            attackerKey.Attest(attackerCeremony.Options), "Attacker's key");

        Assert.True(factory.BeforeCommand.Fired, "the victim's registration never ran inside the attacker's");
        Assert.Equal((HttpStatusCode.BadRequest, "Auth.PasskeyRefused"), await TitleOf(response));
        var row = await factory.WithTenantScopeAsync(victimAccount, db => db.UserPasskeys.AsNoTracking()
            .Where(p => p.UserId == victimId).Select(p => new { p.Data.Name, p.Data.PublicKey }).SingleAsync());
        Assert.Equal("Victim's key", row.Name);
        Assert.Equal(victimKey.PublicKey, row.PublicKey);
        Assert.Equal((0, 0), await factory.PasskeyStateAsync(accountId, attackerId));
        Assert.Equal((1, 1), await factory.PasskeyStateAsync(victimAccount, victimId));
        Assert.False(await CeremonyExistsAsync(attackerCeremony.Id));
    }

    [Fact]
    public Task ADuplicatePasskeyRacingInTheSameFarm_IsRefused_AndChangesNothing() =>
        AssertADuplicatePasskeyRaceIsRefusedAsync(crossFarm: false);

    [Fact]
    public Task ADuplicatePasskeyRacingInAnotherFarm_IsRefused_AndChangesNothing() =>
        AssertADuplicatePasskeyRaceIsRefusedAsync(crossFarm: true);

    // r1 item 4 (G7.4): a locked user's finish is refused and spends the ceremony.
    [Fact]
    public async Task ALockedUsersPasskeyFinish_IsRefused_AndSpendsTheCeremony()
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync(asOwner: false);
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password));
        var ceremony = await StartPasskeyAsync(client);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.LockoutEnd = factory.Clock.GetUtcNow().AddMinutes(15);
            await db.SaveChangesAsync();
        });

        var response = await FinishPasskeyAsync(client, grant, ceremony.Id, new SoftwareAuthenticator().Attest(ceremony.Options));

        Assert.Equal((HttpStatusCode.BadRequest, "Auth.PasskeyRefused"), await TitleOf(response));
        Assert.Equal((0, 0), await factory.PasskeyStateAsync(accountId, userId));
        Assert.False(await CeremonyExistsAsync(ceremony.Id));
    }
}
