using System.Net;
using System.Security.Cryptography;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.IntegrationTests;

public sealed partial class CredentialEpochFloorTests
{
    // #1278 "Re-checks at the code step": an epoch that dropped below 1 between
    // the two steps is refused as an expired challenge, charging and minting
    // nothing. The password step mints a challenge only at an epoch of 1 or
    // more, so the code step's floor and its epoch match each refuse this.
    [Fact]
    public async Task CodeStep_RefusesAnEpochThatDroppedBelowOne_AndChargesNothing()
    {
        var email = $"epoch-floor-tf-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var factor = await factory.EnrolTwoFactorAsync(email);
        var pending = await factory.PasswordStepAsync(email);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.PostSecondFactorAsync(pending.Challenge, WrongCode(factor))).StatusCode);
        var charged = await CountersAsync(factor);
        Assert.Equal((1, 1), (charged.AccessFailedCount, charged.SecondFactorFailures));
        await factory.WithTenantScopeAsync(factor.AccountId, async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "AspNetUsers" DROP CONSTRAINT IF EXISTS "CK_AspNetUsers_CredentialEpoch" """);
            return await db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "AspNetUsers" SET "CredentialEpoch" = 0 WHERE "Id" = {factor.UserId}""");
        });

        var response = await factory.PostSecondFactorAsync(pending.Challenge, await factory.NextTotpCodeAsync(email));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(SecondFactorErrorCodes.ChallengeExpired, (await TestHarness.ReadProblemAsync(response)).GetProperty("title").GetString());
        Assert.Equal(charged, await CountersAsync(factor));
        Assert.Empty(await TokenRowsAsync(factor.AccountId, factor.UserId));
    }

    private Task<(int AccessFailedCount, int SecondFactorFailures, long SecondFactorSignIns)> CountersAsync(EnrolledFactor factor) =>
        factory.WithTenantScopeAsync(factor.AccountId, db => db.Users.AsNoTracking()
            .Where(user => user.Id == factor.UserId)
            .Select(user => new ValueTuple<int, int, long>(user.AccessFailedCount, user.SecondFactorFailures, user.SecondFactorSignIns))
            .SingleAsync());

    // Six digits that match no step from -3 to +3, so they are a wrong guess.
    private static string WrongCode(EnrolledFactor factor)
    {
        var key = TotpMatcher.DecodeBase32(factor.Key);
        var step = TotpMatcher.StepAt(DateTimeOffset.UtcNow);
        var near = Enumerable.Range(-3, 7).Select(offset => TotpMatcher.Compute(key, step + offset).ToString("D6")).ToHashSet();
        string code;
        do code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
        while (near.Contains(code));
        return code;
    }
}
