using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1280: an Owner resets another user's two-factor (#1274 decision 17), G5.2,
// and recover-admin clears it (decision 16), G5.3.
public sealed partial class SecondFactorLoginTests
{
    private sealed record FactorState(bool Enabled, int DailyFailures, DateTimeOffset? Since, int Epoch, bool MustChangePassword);

    private Task<FactorState> FactorStateAsync(Guid accountId, Guid userId) =>
        factory.WithTenantScopeAsync(accountId, db => db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new FactorState(u.TwoFactorEnabled, u.SecondFactorFailures, u.SecondFactorFailuresSince,
                u.CredentialEpoch, u.MustChangePassword))
            .SingleAsync());

    private static string ResetPath(Guid userId) => $"/api/v1/users/{userId}/two-factor";

    // A farm with a signed-in Owner, who is unenrolled and steps up with the password.
    private async Task<(Guid AccountId, Guid OwnerId, HttpClient Owner)> FarmWithOwnerAsync()
    {
        var ownerEmail = $"tf-owner-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        var owner = factory.CreateAuthedClient((await factory.LoginAsync(ownerEmail)).AccessToken);
        return (accountId, await UserIdAsync(accountId, ownerEmail), owner);
    }

    private async Task<string> OwnerGrantAsync(HttpClient owner) =>
        (await GrantOf(await StepUpAsync(owner, TestHarness.Password)))!;

    private async Task<(string Email, EnrolledFactor Factor)> EnrolledManagerAsync(Guid accountId)
    {
        var email = $"tf-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, Roles.Manager);
        return (email, await factory.EnrolTwoFactorAsync(email));
    }

    private async Task<string> ThreeWrongCodesAsync(string email, EnrolledFactor factor)
    {
        var pending = await factory.PasswordStepAsync(email);
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await PostCodeAsync(pending.Challenge, WrongCode(factor))).StatusCode);
        Assert.Equal(3, (await CountersAsync(factor)).DailyFailures);
        return pending.Challenge;
    }

    // G5.2
    [Fact]
    public async Task AnOwnerReset_EndsTheTargetsFactor_AndEveryCredentialMintedBeforeIt()
    {
        var (accountId, ownerId, owner) = await FarmWithOwnerAsync();
        var (email, factor) = await EnrolledManagerAsync(accountId);
        var session = await factory.LoginAsync(email);
        var app = await ConnectedAppToken.IssueAsync(factory, session.AccessToken, factor.RecoveryCodes[1]);
        var targetGrant = await GrantOf(await StepUpAsync(
            factory.CreateAuthedClient(session.AccessToken), TestHarness.Password, factor.RecoveryCodes[0]));
        var challenge = await ThreeWrongCodesAsync(email, factor);
        await StartEnrolmentAsync(accountId, factor.UserId);
        var before = await FactorStateAsync(accountId, factor.UserId);

        var response = await DeleteWithGrantAsync(owner, ResetPath(factor.UserId), await OwnerGrantAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await TwoFactorRowsAsync(accountId, factor.UserId));
        var after = await FactorStateAsync(accountId, factor.UserId);
        Assert.Equal((false, 0, (DateTimeOffset?)null), (after.Enabled, after.DailyFailures, after.Since));
        // G3.20 pairs the flag and epoch writes per member, not per user, so
        // the target's own epoch is asserted here.
        Assert.True(after.Epoch > before.Epoch, "the target's epoch did not move");
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostCodeAsync(challenge, CodeAt(factor, 2))).StatusCode);
        Assert.True((await ValidateGrantAsync(accountId, factor.UserId, targetGrant!)).IsFailure, "the target's step-up grant survived");
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(session.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(accountId, session.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await AppProbeAsync(app));
        await AssertEverySessionAndAppEndedAsync(accountId, factor.UserId, freshRefreshRows: 0);
        Assert.Equal([(ownerId, (string?)null)], await AuditRowsAsync(accountId, factor.UserId, AuditActions.UserTwoFactorReset));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOwnerReset_OfAnUnenrolledUser_Is204_AndLeavesTheirSessionWorking(bool enrolmentStarted)
    {
        var (accountId, ownerId, owner) = await FarmWithOwnerAsync();
        var email = $"tf-plain-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, Roles.Manager);
        var userId = await UserIdAsync(accountId, email);
        var session = await factory.LoginAsync(email);
        if (enrolmentStarted) await StartEnrolmentAsync(accountId, userId);
        var before = await FactorStateAsync(accountId, userId);

        var response = await DeleteWithGrantAsync(owner, ResetPath(userId), await OwnerGrantAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await TwoFactorRowsAsync(accountId, userId));
        Assert.Equal(before.Epoch, (await FactorStateAsync(accountId, userId)).Epoch);
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(session.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(accountId, session.RefreshToken)).StatusCode);
        var rows = await AuditRowsAsync(accountId, userId, AuditActions.UserTwoFactorReset);
        if (!enrolmentStarted)
        {
            Assert.Empty(rows);
            return;
        }
        var (actor, details) = Assert.Single(rows);
        Assert.Equal(ownerId, actor);
        Assert.True(JsonDocument.Parse(details!).RootElement.GetProperty("pending").GetBoolean());
    }

    [Fact]
    public async Task AnOwnerReset_OfTheirOwnId_Is400_BeforeTheGrantIsSpent()
    {
        var (accountId, ownerId, owner) = await FarmWithOwnerAsync();
        var grant = await OwnerGrantAsync(owner);

        var withGrant = await DeleteWithGrantAsync(owner, ResetPath(ownerId), grant);
        var withoutGrant = await DeleteWithGrantAsync(owner, ResetPath(ownerId), "not-a-grant");

        foreach (var response in new[] { withGrant, withoutGrant })
            Assert.Equal((HttpStatusCode.BadRequest, "Users.CannotResetOwnTwoFactor"), (response.StatusCode, (await ProblemOf(response)).Title));
        Assert.True((await ValidateGrantAsync(accountId, ownerId, grant)).IsSuccess, "the self-target 400 spent the grant");
    }

    [Fact]
    public async Task ANonOwner_AndAnotherFarmsOwner_CannotResetTheTarget()
    {
        var (accountId, _, _) = await FarmWithOwnerAsync();
        var managerEmail = $"tf-manager-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, managerEmail, Roles.Manager);
        var (_, factor) = await EnrolledManagerAsync(accountId);
        var manager = factory.CreateAuthedClient((await factory.LoginAsync(managerEmail)).AccessToken);
        var (_, _, otherFarmOwner) = await FarmWithOwnerAsync();
        var managerGrant = await GrantOf(await StepUpAsync(manager, TestHarness.Password));

        var byManager = await DeleteWithGrantAsync(manager, ResetPath(factor.UserId), managerGrant!);
        var byOtherFarm = await DeleteWithGrantAsync(otherFarmOwner, ResetPath(factor.UserId), await OwnerGrantAsync(otherFarmOwner));

        Assert.Equal(HttpStatusCode.Forbidden, byManager.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byOtherFarm.StatusCode);
        Assert.True((await FactorStateAsync(accountId, factor.UserId)).Enabled);
        Assert.Equal(1 + factor.RecoveryCodes.Count, (await TwoFactorRowsAsync(accountId, factor.UserId)).Count);
    }

    // The authorization middleware checked the role before the request reached
    // the account lock. An Owner demoted or disabled while the request waits
    // for that lock is refused by the re-check inside it.
    [Theory]
    [InlineData("demoted")]
    [InlineData("disabled")]
    public async Task AnOwnerDemotedOrDisabledWhileTheRequestWaits_IsRefused(string change)
    {
        var (accountId, ownerId, _) = await FarmWithOwnerAsync();
        var actorEmail = $"tf-owner2-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, actorEmail, Roles.Owner);
        var actorId = await UserIdAsync(accountId, actorEmail);
        var actor = factory.CreateAuthedClient((await factory.LoginAsync(actorEmail)).AccessToken);
        var grant = await OwnerGrantAsync(actor);
        var (_, factor) = await EnrolledManagerAsync(accountId);
        factory.BeforeCommand.Arm("FOR UPDATE", async () =>
        {
            using var scope = factory.Services.CreateScope().ResolveTenantAndActor(accountId, ownerId);
            var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();
            var done = change == "disabled"
                ? await identity.DisableUserAsync(accountId, actorId, ownerId, "test")
                : await identity.ChangeUserRoleAsync(accountId, actorId, Roles.Manager, ownerId);
            Assert.True(done.IsSuccess, change);
        });

        var response = await DeleteWithGrantAsync(actor, ResetPath(factor.UserId), grant);

        Assert.True(factory.BeforeCommand.Fired, "the concurrent change never ran");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(AppError.Forbidden().Code, (await ProblemOf(response)).Title);
        Assert.True((await FactorStateAsync(accountId, factor.UserId)).Enabled);
        Assert.Equal(1 + factor.RecoveryCodes.Count, (await TwoFactorRowsAsync(accountId, factor.UserId)).Count);
    }

    // G5.3, in process; RecoverAdminCommandTests runs the verb under the
    // ephemeral key ring and the DML-only role.
    [Fact]
    public async Task RecoverAdmin_ClearsTwoFactor_ForcesThePasswordChange_AndEndsEveryCredential()
    {
        var email = $"tf-owner-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var factor = await factory.EnrolTwoFactorAsync(email);
        var session = await factory.LoginAsync(email);
        var app = await ConnectedAppToken.IssueAsync(factory, session.AccessToken, factor.RecoveryCodes[0]);
        await ThreeWrongCodesAsync(email, factor);

        string password;
        using (var scope = factory.Services.CreateScope())
        {
            var recovered = await scope.ServiceProvider.GetRequiredService<AdminRecoveryService>()
                .RecoverAsync(email, accountId, reason: "lost phone");
            Assert.True(recovered.IsSuccess, recovered.IsFailure ? recovered.Error.Description : "");
            password = recovered.Value.TemporaryPassword;
        }

        Assert.Empty(await TwoFactorRowsAsync(accountId, factor.UserId));
        var state = await FactorStateAsync(accountId, factor.UserId);
        Assert.Equal((false, 0, (DateTimeOffset?)null, true), (state.Enabled, state.DailyFailures, state.Since, state.MustChangePassword));
        var (_, details) = Assert.Single(await AuditRowsAsync(accountId, factor.UserId, AuditActions.UserBreakGlassReset));
        Assert.True(JsonDocument.Parse(details!).RootElement.GetProperty("twoFactorCleared").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(session.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await AppProbeAsync(app));

        // The temporary password signs in with no code, and is held at the forced change.
        var signedIn = await factory.TryLoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        var gated = await GetMeAsync((await TestHarness.ReadTokensAsync(signedIn)).AccessToken);
        Assert.Equal((HttpStatusCode.Forbidden, "Auth.MustChangePassword"), (gated.StatusCode, (await ProblemOf(gated)).Title));
    }
}
