namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[Collection(IntegrationCollection.Name)]
public sealed class ChangeUserEmailTests(CluckworkWebApplicationFactory factory)
{
    private sealed record UserRow(Guid Id, string Email, string? DisplayName, string Role, DateTimeOffset? DisabledAt);
    private sealed record StepUpDto(string Token, DateTimeOffset ExpiresAt);
    private sealed record IdentitySnapshot(
        string? Email,
        string? NormalizedEmail,
        string? UserName,
        string? NormalizedUserName,
        string? SecurityStamp,
        string? ConcurrencyStamp,
        int CredentialEpoch,
        bool EmailConfirmed,
        DateTimeOffset? DisabledAt,
        int AuditCount,
        IReadOnlyList<DateTimeOffset?> RefreshRevocations);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}@test.local";

    private async Task<(HttpClient Owner, Guid AccountId, string Email, Guid Id, string FarmCode)> OwnerAsync()
    {
        var email = Unique("owner");
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var owner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var id = await UserIdAsync(accountId, email);
        var farmCode = await factory.WithTenantScopeAsync(accountId,
            db => db.Accounts.Where(a => a.Id == accountId).Select(a => a.Slug).SingleAsync());
        return (owner, accountId, email, id, farmCode);
    }

    private async Task<Guid> SeedUserAsync(Guid accountId, string email, string? role = "Manager")
    {
        await factory.SeedUserAsync(accountId, email, role);
        return await UserIdAsync(accountId, email);
    }

    private Task<Guid> UserIdAsync(Guid accountId, string email) =>
        factory.WithTenantScopeAsync(accountId, db => db.Users
            .Where(u => u.AccountId == accountId && u.Email == email)
            .Select(u => u.Id)
            .SingleAsync());

    private static async Task<string> StepUpAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/step-up", new { password = TestHarness.Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StepUpDto>())!.Token;
    }

    private static Task<HttpResponseMessage> ChangeEmailAsync(
        HttpClient client, Guid userId, string email, string? stepUpToken = null, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{userId}/email")
        {
            Content = JsonContent.Create(new { email })
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        if (stepUpToken is not null)
            request.Headers.Add(AuthEndpoints.StepUpHeaderName, stepUpToken);
        return client.SendAsync(request);
    }

    private Task<IdentitySnapshot> SnapshotAsync(Guid accountId, Guid userId) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            var row = await db.Users.SingleAsync(u => u.Id == userId && u.AccountId == accountId);
            return new IdentitySnapshot(
                row.Email,
                row.NormalizedEmail,
                row.UserName,
                row.NormalizedUserName,
                row.SecurityStamp,
                row.ConcurrencyStamp,
                row.CredentialEpoch,
                row.EmailConfirmed,
                row.DisabledAt,
                await db.AuditEvents.CountAsync(e =>
                    e.EntityId == userId && e.Action == "User.EmailChanged"),
                await db.RefreshTokens.Where(t => t.UserId == userId)
                    .OrderBy(t => t.Id)
                    .Select(t => t.RevokedAt)
                    .ToListAsync());
        });

    private static void AssertSnapshotEqual(IdentitySnapshot expected, IdentitySnapshot actual)
    {
        Assert.Equal(expected.Email, actual.Email);
        Assert.Equal(expected.NormalizedEmail, actual.NormalizedEmail);
        Assert.Equal(expected.UserName, actual.UserName);
        Assert.Equal(expected.NormalizedUserName, actual.NormalizedUserName);
        Assert.Equal(expected.SecurityStamp, actual.SecurityStamp);
        Assert.Equal(expected.ConcurrencyStamp, actual.ConcurrencyStamp);
        Assert.Equal(expected.CredentialEpoch, actual.CredentialEpoch);
        Assert.Equal(expected.EmailConfirmed, actual.EmailConfirmed);
        Assert.Equal(expected.DisabledAt, actual.DisabledAt);
        Assert.Equal(expected.AuditCount, actual.AuditCount);
        Assert.Equal(expected.RefreshRevocations, actual.RefreshRevocations);
    }

    private Task<HttpResponseMessage> TryLoginAsync(string farmCode, string email) =>
        factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login", new { farmCode, email, password = TestHarness.Password });

    [Fact]
    public async Task Change_WritesAllFourColumnsThroughTheConfiguredNormalizer()
    {
        var (owner, accountId, _, _, _) = await OwnerAsync();
        var oldEmail = Unique("target");
        var targetId = await SeedUserAsync(accountId, oldEmail);
        var proof = await StepUpAsync(owner);

        var response = await ChangeEmailAsync(owner, targetId, "  Case.Change@Farm.Test  ", proof);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var row = await db.Users.SingleAsync(u => u.Id == targetId && u.AccountId == accountId);
        const string expectedEmail = "Case.Change@Farm.Test";
        Assert.Equal(expectedEmail, row.Email);
        Assert.Equal(userManager.NormalizeEmail(expectedEmail), row.NormalizedEmail);
        Assert.Equal(expectedEmail, row.UserName);
        Assert.Equal(userManager.NormalizeName(expectedEmail), row.NormalizedUserName);
    }

    [Fact]
    public async Task CrossAccountDuplicate_SucceedsInBothFarms()
    {
        var duplicate = Unique("shared");
        var (_, accountA, _, _, _) = await OwnerAsync();
        await SeedUserAsync(accountA, duplicate);
        var (ownerB, accountB, _, _, _) = await OwnerAsync();
        var targetId = await SeedUserAsync(accountB, Unique("before"));

        var response = await ChangeEmailAsync(ownerB, targetId, duplicate, await StepUpAsync(ownerB));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, await factory.WithTenantScopeAsync(accountA,
            db => db.Users.CountAsync(u => u.AccountId == accountA && u.Email == duplicate)));
        Assert.Equal(1, await factory.WithTenantScopeAsync(accountB,
            db => db.Users.CountAsync(u => u.AccountId == accountB && u.Email == duplicate)));
    }

    [Fact]
    public async Task SameAccountDuplicate_Is409UsersDuplicateEmail()
    {
        var (owner, accountId, _, _, _) = await OwnerAsync();
        var existing = Unique("existing");
        await SeedUserAsync(accountId, existing);
        var targetId = await SeedUserAsync(accountId, Unique("target"));

        var response = await ChangeEmailAsync(owner, targetId, existing, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Users.DuplicateEmail", (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task Change_BumpsEpochOnce_AndKillsTheLiveAccessAndRefreshTokens()
    {
        var (owner, accountId, _, _, _) = await OwnerAsync();
        var targetEmail = Unique("target");
        var targetId = await SeedUserAsync(accountId, targetEmail);
        var session = await factory.LoginAsync(targetEmail);
        var targetClient = factory.CreateAuthedClient(session.AccessToken);
        var before = await SnapshotAsync(accountId, targetId);

        var response = await ChangeEmailAsync(owner, targetId, Unique("changed"), await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = await SnapshotAsync(accountId, targetId);
        Assert.Equal(before.CredentialEpoch + 1, after.CredentialEpoch);
        Assert.Equal(HttpStatusCode.Unauthorized, (await targetClient.GetAsync("/api/v1/flocks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostRefreshAsync(
                session.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
        Assert.NotEmpty(after.RefreshRevocations);
        Assert.All(after.RefreshRevocations, value => Assert.NotNull(value));
    }

    [Fact]
    public async Task NewEmailLogsIn_OldEmailFails_AndOldEmailCanBeReusedInTheFarm()
    {
        var (owner, accountId, _, _, farmCode) = await OwnerAsync();
        var oldEmail = Unique("old");
        var newEmail = Unique("new");
        var targetId = await SeedUserAsync(accountId, oldEmail);

        var response = await ChangeEmailAsync(owner, targetId, newEmail, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TryLoginAsync(farmCode, newEmail)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLoginAsync(farmCode, oldEmail)).StatusCode);
        await factory.SeedUserAsync(accountId, oldEmail, role: null);
        Assert.Equal(1, await factory.WithTenantScopeAsync(accountId,
            db => db.Users.CountAsync(u => u.AccountId == accountId && u.Email == oldEmail)));
    }

    [Fact]
    public async Task ExactTrimmedNoOp_LeavesEpochStampsTokensAndAuditUnchanged()
    {
        var (owner, accountId, _, _, _) = await OwnerAsync();
        var targetEmail = Unique("target");
        var targetId = await SeedUserAsync(accountId, targetEmail);
        await factory.LoginAsync(targetEmail);
        var before = await SnapshotAsync(accountId, targetId);

        var response = await ChangeEmailAsync(owner, targetId, $"  {targetEmail}  ", await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertSnapshotEqual(before, await SnapshotAsync(accountId, targetId));
    }

    [Fact]
    public async Task CaseOnlyCorrection_IsARealChange()
    {
        var (owner, accountId, _, _, _) = await OwnerAsync();
        var original = $"Case-{Guid.NewGuid():N}@test.local";
        var targetId = await SeedUserAsync(accountId, original);
        var before = await SnapshotAsync(accountId, targetId);
        var corrected = original.ToLowerInvariant();

        var response = await ChangeEmailAsync(owner, targetId, corrected, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = await SnapshotAsync(accountId, targetId);
        Assert.Equal(corrected, after.Email);
        Assert.Equal(before.CredentialEpoch + 1, after.CredentialEpoch);
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
    }

    [Fact]
    public async Task SoleOwnerSelfChange_Is422_AndNamesAddingASecondOwner()
    {
        var (owner, accountId, oldEmail, ownerId, _) = await OwnerAsync();
        var before = await SnapshotAsync(accountId, ownerId);

        var response = await ChangeEmailAsync(owner, ownerId, Unique("new-owner"), await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("add a second Owner first", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        AssertSnapshotEqual(before, await SnapshotAsync(accountId, ownerId));
        Assert.Equal(oldEmail, (await SnapshotAsync(accountId, ownerId)).Email);
    }

    [Fact]
    public async Task SelfChange_WithAnotherActiveOwner_Succeeds()
    {
        var (owner, accountId, _, ownerId, _) = await OwnerAsync();
        await SeedUserAsync(accountId, Unique("co-owner"), Cluckwork.Domain.Accounts.Roles.Owner);
        var newEmail = Unique("new-owner");

        var response = await ChangeEmailAsync(owner, ownerId, newEmail, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(newEmail, (await SnapshotAsync(accountId, ownerId)).Email);
    }

    [Fact]
    public async Task ForeignUserId_Is404_AndForeignRowIsUnchanged()
    {
        var (foreignOwner, foreignAccount, _, _, _) = await OwnerAsync();
        foreignOwner.Dispose();
        var foreignEmail = Unique("foreign");
        var foreignId = await SeedUserAsync(foreignAccount, foreignEmail);
        var foreignBefore = await SnapshotAsync(foreignAccount, foreignId);
        var (owner, _, _, _, _) = await OwnerAsync();

        var response = await ChangeEmailAsync(owner, foreignId, Unique("attack"), await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertSnapshotEqual(foreignBefore, await SnapshotAsync(foreignAccount, foreignId));
    }

    [Fact]
    public async Task DisabledTarget_CanBeCorrected_AndStaysDisabled()
    {
        var (owner, accountId, _, ownerId, _) = await OwnerAsync();
        var targetId = await SeedUserAsync(accountId, Unique("disabled"));
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var target = await db.Users.SingleAsync(u => u.Id == targetId && u.AccountId == accountId);
            target.DisabledAt = DateTimeOffset.UtcNow;
            target.DisabledBy = ownerId;
            await db.SaveChangesAsync();
        });
        var disabledAt = (await SnapshotAsync(accountId, targetId)).DisabledAt;
        var newEmail = Unique("corrected");

        var response = await ChangeEmailAsync(owner, targetId, newEmail, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = await SnapshotAsync(accountId, targetId);
        Assert.Equal(newEmail, after.Email);
        Assert.Equal(disabledAt, after.DisabledAt);
    }

    [Fact]
    public async Task Change_PreservesEmailConfirmed()
    {
        var (owner, accountId, _, _, _) = await OwnerAsync();
        var targetId = await SeedUserAsync(accountId, Unique("confirmed"));
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var target = await db.Users.SingleAsync(u => u.Id == targetId && u.AccountId == accountId);
            target.EmailConfirmed = true;
            await db.SaveChangesAsync();
        });

        var response = await ChangeEmailAsync(owner, targetId, Unique("changed"), await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True((await SnapshotAsync(accountId, targetId)).EmailConfirmed);
    }

    [Fact]
    public async Task Change_AuditsOldAndNewEmail_OnlyOnARealChange()
    {
        var (owner, accountId, _, _, _) = await OwnerAsync();
        var oldEmail = Unique("old");
        var newEmail = Unique("new");
        var targetId = await SeedUserAsync(accountId, oldEmail);

        var changed = await ChangeEmailAsync(owner, targetId, newEmail, await StepUpAsync(owner));
        var noOp = await ChangeEmailAsync(owner, targetId, $" {newEmail} ", await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, noOp.StatusCode);
        var events = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(e => e.EntityId == targetId && e.Action == "User.EmailChanged")
            .ToListAsync());
        var audit = Assert.Single(events);
        Assert.Contains(oldEmail, audit.DetailsJson);
        Assert.Contains(newEmail, audit.DetailsJson);
    }

    [Fact]
    public async Task Change_InvalidatesTheTargetsOutstandingStepUpGrant()
    {
        var (owner, accountId, _, _, _) = await OwnerAsync();
        var targetEmail = Unique("target");
        var targetId = await SeedUserAsync(accountId, targetEmail);
        var targetClient = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(targetEmail));
        var targetGrant = await StepUpAsync(targetClient);

        var response = await ChangeEmailAsync(owner, targetId, Unique("changed"), await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var stepUp = scope.ServiceProvider.GetRequiredService<IStepUpGrantService>();
        Assert.True((await stepUp.ValidateAsync(
            accountId, targetId, targetGrant, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task MissingStepUp_Is403_AndLeavesIdentityColumnsUnchanged()
    {
        var (owner, accountId, _, _, _) = await OwnerAsync();
        var targetId = await SeedUserAsync(accountId, Unique("target"));
        var before = await SnapshotAsync(accountId, targetId);

        var response = await ChangeEmailAsync(owner, targetId, Unique("changed"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertSnapshotEqual(before, await SnapshotAsync(accountId, targetId));
    }

    [Fact]
    public async Task InvalidEmail_Is400WithEmailFieldCode()
    {
        var (owner, accountId, _, _, _) = await OwnerAsync();
        var targetId = await SeedUserAsync(accountId, Unique("target"));

        var response = await ChangeEmailAsync(owner, targetId, "not-an-email");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("email", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("User.Email.Format", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedBody_Is413()
    {
        var (owner, accountId, _, _, _) = await OwnerAsync();
        var targetId = await SeedUserAsync(accountId, Unique("target"));
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{targetId}/email")
        {
            Content = JsonContent.Create(new { email = new string('a', 4096) })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await owner.SendAsync(request)).StatusCode);
    }
}
