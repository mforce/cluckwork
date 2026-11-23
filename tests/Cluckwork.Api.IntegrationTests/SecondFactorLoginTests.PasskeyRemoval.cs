using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1284: renaming and removing passkeys (record 1282 decisions 10 and 16),
// G7.5 and G7.13.
public sealed partial class SecondFactorLoginTests
{
    private static string UserPasskeysPath(Guid userId) => $"/api/v1/users/{userId}/passkeys";

    private static Task<HttpResponseMessage> RenamePasskeyAsync(HttpClient client, string id, string name) =>
        client.PatchAsJsonAsync($"{PasskeysPath}/{id}", new { name });

    private static Task<HttpResponseMessage> RemovePasskeyAsync(HttpClient client, string id, string? grant)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"{PasskeysPath}/{id}");
        if (grant is not null) request.Headers.Add(Modules.Access.Auth.AuthEndpoints.StepUpHeaderName, grant);
        return client.SendAsync(request);
    }

    private Task<List<(string Id, string? Name)>> PasskeysOfAsync(Guid accountId, Guid userId) =>
        factory.WithTenantScopeAsync(accountId, async db => (await db.UserPasskeys.AsNoTracking()
            .Where(p => p.UserId == userId).Select(p => new { p.CredentialId, p.Data.Name }).ToListAsync())
            .Select(p => (WebEncoders.Base64UrlEncode(p.CredentialId), p.Name)).OrderBy(p => p.Item1).ToList());

    // A user with the app and one passkey, signed in twice: the app user steps
    // up with the password and a code, so #1443 leaves these cases as they are.
    private async Task<(EnrolledFactor Factor, HttpClient Client, TokenPairDto Other, string PasskeyId)> AppUserWithPasskeyAsync()
    {
        var (email, factor) = await SeedEnrolledAsync(asOwner: false);
        var client = factory.CreateAuthedClient((await factory.LoginAsync(email)).AccessToken);
        await SeedPasskeysAsync(factor.AccountId, factor.UserId, 1);
        var other = await factory.LoginAsync(email);
        return (factor, client, other, (await PasskeysOfAsync(factor.AccountId, factor.UserId)).Single().Id);
    }

    // G7.5: a user's removal of their own passkey ends every other session and
    // connected app and keeps this device.
    [Fact]
    public async Task RemovingYourOwnPasskey_EndsEveryOtherSession_AndKeepsThisDevice()
    {
        var (factor, client, other, id) = await AppUserWithPasskeyAsync();
        var app = await ConnectedAppToken.IssueAsync(factory, other.AccessToken, factor.RecoveryCodes[1]);
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password, factor.RecoveryCodes[0]));

        var response = await RemovePasskeyAsync(client, id, grant);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fresh = await TestHarness.ReadTokensAsync(response);
        Assert.Equal((0, 0), await PasskeyStateAsync(factor.AccountId, factor.UserId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(other.AccessToken)).StatusCode);
        await AssertEverySessionAndAppEndedAsync(factor.AccountId, factor.UserId, freshRefreshRows: 1);
        Assert.Equal(HttpStatusCode.Unauthorized, await AppProbeAsync(app));
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(fresh.AccessToken)).StatusCode);
        Assert.Single(await AuditRowsAsync(factor.AccountId, factor.UserId, AuditActions.UserPasskeyRemoved));
    }

    // Renaming ends nothing and needs no grant.
    [Fact]
    public async Task RenamingAPasskey_ChangesOnlyItsName()
    {
        var (factor, client, other, id) = await AppUserWithPasskeyAsync();

        var response = await RenamePasskeyAsync(client, id, "  Work laptop  ");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal([(id, (string?)"Work laptop")], await PasskeysOfAsync(factor.AccountId, factor.UserId));
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(other.AccessToken)).StatusCode);
        var list = await (await client.GetAsync(PasskeysPath)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(list.GetProperty("enabled").GetBoolean());
        Assert.Equal("Work laptop", list.GetProperty("passkeys")[0].GetProperty("name").GetString());
    }

    // G7.13: another user's credential id, in this farm or another, answers
    // 404 and changes nothing.
    [Fact]
    public async Task RenamingOrRemovingAnotherUsersPasskey_Is404_AndChangesNothing()
    {
        var (factor, client, _, _) = await AppUserWithPasskeyAsync();
        var (_, accountId, userId, _, _) = await SignedInPlainAsync(asOwner: false);
        var sameFarmEmail = $"pk-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(factor.AccountId, sameFarmEmail, Roles.Manager);
        var sameFarmUser = await UserIdAsync(factor.AccountId, sameFarmEmail);
        await SeedPasskeysAsync(accountId, userId, 1);
        await SeedPasskeysAsync(factor.AccountId, sameFarmUser, 1);
        var targets = new[] { (accountId, userId), (factor.AccountId, sameFarmUser) };

        foreach (var ((targetAccount, targetUser), code) in targets.Zip(factor.RecoveryCodes))
        {
            var before = await PasskeysOfAsync(targetAccount, targetUser);
            var id = before.Single().Id;

            var renamed = await RenamePasskeyAsync(client, id, "Mine now");
            var removed = await RemovePasskeyAsync(client, id,
                await GrantOf(await StepUpAsync(client, TestHarness.Password, code)));

            Assert.Equal(HttpStatusCode.NotFound, renamed.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
            Assert.Equal(before, await PasskeysOfAsync(targetAccount, targetUser));
            Assert.Equal((1, 1), await PasskeyStateAsync(targetAccount, targetUser));
        }
        Assert.Equal((1, 1), await PasskeyStateAsync(factor.AccountId, factor.UserId));
    }

    // G7.13: two concurrent removals of one passkey, made directly (over HTTP
    // the acting-epoch comparison refuses the second), remove it once. Both
    // reach the user-row lock before either deletes.
    [Fact]
    public async Task TwoConcurrentRemovalsOfOnePasskey_RemoveItOnce()
    {
        var (_, accountId, userId, _, _) = await SignedInPlainAsync(asOwner: false);
        await SeedPasskeysAsync(accountId, userId, 2);
        var credentialId = WebEncoders.Base64UrlDecode((await PasskeysOfAsync(accountId, userId))[0].Id);
        var arrived = 0;
        var both = new TaskCompletionSource();
        factory.BeforeCommand.Arm("FOR NO KEY UPDATE", async () =>
        {
            if (Interlocked.Increment(ref arrived) == 2) both.SetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }, times: 2);

        async Task<RemoveOutcome> RemoveAsync()
        {
            using var scope = factory.Services.CreateScope().ResolveTenantAndActor(accountId, userId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var passkeys = scope.ServiceProvider.GetRequiredService<IPasskeyCredentials>();
            return await TestHarness.InTransactionAsync(db, ct => passkeys.RemoveAsync(accountId, userId, credentialId, ct));
        }

        var outcomes = await Task.WhenAll(Task.Run(RemoveAsync), Task.Run(RemoveAsync));

        Assert.Equal([RemoveOutcome.NotFound, RemoveOutcome.Removed], outcomes.Order());
        Assert.Equal((1, 1), await PasskeyStateAsync(accountId, userId));
    }

    // G7.5: an Owner's removal ends every session of the target.
    [Fact]
    public async Task AnOwnersPasskeyRemoval_EndsEverySessionOfTheTarget()
    {
        var (accountId, ownerId, owner) = await FarmWithOwnerAsync();
        var email = $"pk-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, Roles.Manager);
        var userId = await UserIdAsync(accountId, email);
        await SeedPasskeysAsync(accountId, userId, 2);
        var session = await factory.LoginAsync(email);
        var app = await ConnectedAppToken.IssueAsync(factory, session.AccessToken);

        var response = await DeleteWithGrantAsync(owner, UserPasskeysPath(userId), await OwnerGrantAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal((0, 0), await PasskeyStateAsync(accountId, userId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(session.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(accountId, session.RefreshToken)).StatusCode);
        await AssertEverySessionAndAppEndedAsync(accountId, userId, freshRefreshRows: 0);
        Assert.Equal(HttpStatusCode.Unauthorized, await AppProbeAsync(app));
        var (actor, details) = Assert.Single(await AuditRowsAsync(accountId, userId, AuditActions.UserPasskeysRemovedByOwner));
        Assert.Equal(ownerId, actor);
        Assert.Equal(2, JsonDocument.Parse(details!).RootElement.GetProperty("count").GetInt32());
        var listed = await (await owner.GetAsync("/api/v1/users")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, listed.EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == userId).GetProperty("passkeyCount").GetInt32());
    }

    // G7.5: the Owner's own id is refused before the grant is spent.
    [Fact]
    public async Task AnOwnersPasskeyRemoval_OfTheirOwnId_Is400_BeforeTheGrantIsSpent()
    {
        var (accountId, ownerId, owner) = await FarmWithOwnerAsync();
        await SeedPasskeysAsync(accountId, ownerId, 1);
        var grant = await OwnerGrantAsync(owner);

        var response = await DeleteWithGrantAsync(owner, UserPasskeysPath(ownerId), grant);

        Assert.Equal((HttpStatusCode.BadRequest, "Users.CannotRemoveOwnPasskeys"), await TitleOf(response));
        Assert.True((await ValidateGrantAsync(accountId, ownerId, grant)).IsSuccess, "the self-target 400 spent the grant");
        Assert.Equal((1, 1), await PasskeyStateAsync(accountId, ownerId));
    }

    // G7.5: another farm's user is 404, and an Owner demoted while the
    // request waits for the account lock is refused.
    [Fact]
    public async Task AnOwnersPasskeyRemoval_RefusesAnotherFarmsUser_AndADemotedActor()
    {
        var (accountId, ownerId, _) = await FarmWithOwnerAsync();
        var email = $"pk-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, Roles.Manager);
        var userId = await UserIdAsync(accountId, email);
        await SeedPasskeysAsync(accountId, userId, 1);
        var (_, _, otherFarmOwner) = await FarmWithOwnerAsync();
        var actorEmail = $"pk-owner2-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, actorEmail, Roles.Owner);
        var actorId = await UserIdAsync(accountId, actorEmail);
        var actor = factory.CreateAuthedClient((await factory.LoginAsync(actorEmail)).AccessToken);
        var actorGrant = await OwnerGrantAsync(actor);

        var byOtherFarm = await DeleteWithGrantAsync(otherFarmOwner, UserPasskeysPath(userId), await OwnerGrantAsync(otherFarmOwner));
        factory.BeforeCommand.Arm("FOR UPDATE", async () =>
        {
            using var scope = factory.Services.CreateScope().ResolveTenantAndActor(accountId, ownerId);
            var done = await scope.ServiceProvider.GetRequiredService<IIdentityProvider>()
                .ChangeUserRoleAsync(accountId, actorId, Roles.Manager, ownerId);
            Assert.True(done.IsSuccess);
        });
        var byDemoted = await DeleteWithGrantAsync(actor, UserPasskeysPath(userId), actorGrant);

        Assert.Equal(HttpStatusCode.NotFound, byOtherFarm.StatusCode);
        Assert.True(factory.BeforeCommand.Fired, "the concurrent demotion never ran");
        Assert.Equal((HttpStatusCode.Forbidden, AppError.Forbidden().Code), await TitleOf(byDemoted));
        Assert.Equal((1, 1), await PasskeyStateAsync(accountId, userId));
    }

    // G7.10: an Owner's password reset and the user's own password change
    // leave every passkey.
    [Fact]
    public async Task APasswordResetOrChange_LeavesEveryPasskey()
    {
        var (accountId, _, owner) = await FarmWithOwnerAsync();
        var email = $"pk-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, Roles.Manager);
        var userId = await UserIdAsync(accountId, email);
        await SeedPasskeysAsync(accountId, userId, 2);
        var before = await PasskeysOfAsync(accountId, userId);

        var reset = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{userId}/password")
        {
            Content = JsonContent.Create(new { newPassword = TestHarness.Password }),
        };
        reset.Headers.Add(Modules.Access.Auth.AuthEndpoints.StepUpHeaderName, await OwnerGrantAsync(owner));
        reset.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(reset)).StatusCode);
        var user = factory.CreateAuthedClient((await factory.LoginAsync(email)).AccessToken);
        var change = await user.PostWithKeyAsync("/api/v1/auth/change-password", Guid.NewGuid().ToString(),
            new { currentPassword = TestHarness.Password, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.Equal(before, await PasskeysOfAsync(accountId, userId));
        Assert.Equal((2, 2), await PasskeyStateAsync(accountId, userId));
    }

    // r1 item 8: a name with a control character is a 400 at registration and
    // rename, before anything is saved or consumed.
    [Fact]
    public async Task APasskeyNameWithAControlCharacter_Is400_AtRegistrationAndRename()
    {
        var (factor, client, _, id) = await AppUserWithPasskeyAsync();
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password, factor.RecoveryCodes[0]));
        var ceremony = await StartPasskeyAsync(client);

        var registered = await FinishPasskeyAsync(client, grant, ceremony.Id, new SoftwareAuthenticator().Attest(ceremony.Options), "Pho\0ne");
        var renamed = await RenamePasskeyAsync(client, id, "Lap\ntop");

        Assert.Equal(HttpStatusCode.BadRequest, registered.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
        Assert.Equal((1, 1), await PasskeyStateAsync(factor.AccountId, factor.UserId));
        Assert.True(await CeremonyExistsAsync(ceremony.Id), "a refused name consumed the ceremony");
        Assert.Equal([(id, (string?)"Seeded 0")], await PasskeysOfAsync(factor.AccountId, factor.UserId));
    }
}
