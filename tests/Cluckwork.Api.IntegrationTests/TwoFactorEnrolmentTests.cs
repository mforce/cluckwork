using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1278: StartEnrolmentAsync and ConfirmEnrolmentAsync, with no routes yet
// (#1274 "Enrolling and replacing a key"). #1277 maps them.
[Collection(SecondFactorCollection.Name)]
public sealed class TwoFactorEnrolmentTests(SecondFactorFactory factory) : IDisposable
{
    public void Dispose() => factory.Clock.Reset();

    private sealed record Seeded(string Email, Guid AccountId, Guid UserId);

    private async Task<Seeded> SeedAsync()
    {
        var email = $"enrol-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await factory.WithTenantScopeAsync(accountId, db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        return new Seeded(email, accountId, userId);
    }

    private async Task<T> InTenantAsync<T>(Seeded user, Func<ITwoFactorCredentials, AppDbContext, CancellationToken, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(user.AccountId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var credentials = scope.ServiceProvider.GetRequiredService<ITwoFactorCredentials>();
        return await TestHarness.InTransactionAsync(db, ct => work(credentials, db, ct));
    }

    private Task<EnrolmentStart> StartAsync(Seeded user) =>
        InTenantAsync(user, (credentials, _, ct) => credentials.StartEnrolmentAsync(user.AccountId, user.UserId, ct));

    private Task<ConfirmOutcome> ConfirmAsync(Seeded user, int code) =>
        InTenantAsync(user, (credentials, _, ct) =>
            credentials.ConfirmEnrolmentAsync(user.AccountId, user.UserId, new SecondFactorCode.Totp(code), ct));

    private int CodeFor(EnrolmentStart start, int stepOffset = 0) =>
        TotpMatcher.Compute(TotpMatcher.DecodeBase32(start.Key), TotpMatcher.StepAt(factory.Clock.GetUtcNow()) + stepOffset);

    private int WrongCodeFor(EnrolmentStart start)
    {
        var near = Enumerable.Range(-2, 5).Select(offset => CodeFor(start, offset)).ToHashSet();
        var code = 0;
        while (near.Contains(code)) code++;
        return code;
    }

    private Task<List<string?>> PendingValuesAsync(Seeded user) =>
        factory.WithTenantScopeAsync(user.AccountId, db => db.UserTokens.AsNoTracking()
            .Where(t => t.UserId == user.UserId && t.LoginProvider == "Cluckwork" && t.Name == "PendingAuthenticator")
            .Select(t => t.Value).ToListAsync());

    private Task<ApplicationUser> UserAsync(Seeded user) =>
        factory.WithTenantScopeAsync(user.AccountId, db => db.Users.AsNoTracking().SingleAsync(u => u.Id == user.UserId));

    // G3.22
    [Fact]
    public async Task AStartThatIsNeverConfirmed_LeavesLoginOneStep_AndTheNextStartReplacesThePayload()
    {
        var user = await SeedAsync();
        await StartAsync(user);
        var first = Assert.Single(await PendingValuesAsync(user));

        Assert.False((await UserAsync(user)).TwoFactorEnabled);
        Assert.Equal(HttpStatusCode.OK, (await factory.TryLoginAsync(user.Email, TestHarness.Password)).StatusCode);

        await StartAsync(user);
        Assert.NotEqual(first, Assert.Single(await PendingValuesAsync(user)));
    }

    // G3.23: four checks stand behind this, so it pins the end-to-end invariant.
    [Fact]
    public async Task TwoConcurrentConfirmsOfOnePayload_LeaveOneEnrolment()
    {
        var user = await SeedAsync();
        var start = await StartAsync(user);
        var code = CodeFor(start);

        var outcomes = await Task.WhenAll(ConfirmAsync(user, code), ConfirmAsync(user, code));

        Assert.Single(outcomes, outcome => outcome is ConfirmOutcome.Enrolled);
        Assert.Equal(10, await factory.WithTenantScopeAsync(user.AccountId, db => db.UserTokens
            .CountAsync(t => t.UserId == user.UserId && t.Name.StartsWith("RecoveryCode:"))));
    }

    // G3.24
    [Fact]
    public async Task APayloadOlderThanTenMinutes_IsRefused()
    {
        var user = await SeedAsync();
        var start = await StartAsync(user);

        factory.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        Assert.IsType<ConfirmOutcome.Superseded>(await ConfirmAsync(user, CodeFor(start)));
        Assert.False((await UserAsync(user)).TwoFactorEnabled);
    }

    [Fact]
    public async Task AnEpochOnlyChange_BetweenStartAndConfirm_IsRefused()
    {
        var user = await SeedAsync();
        var start = await StartAsync(user);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Id == user.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.CredentialEpoch, u => u.CredentialEpoch + 1));

        Assert.IsType<ConfirmOutcome.Superseded>(await ConfirmAsync(user, CodeFor(start)));
    }

    [Fact]
    public async Task AStampOnlyChange_BetweenStartAndConfirm_IsRefused()
    {
        var user = await SeedAsync();
        var start = await StartAsync(user);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.UpdateSecurityStampAsync((await users.FindByIdAsync(user.UserId.ToString()))!)).Succeeded);
        }

        Assert.IsType<ConfirmOutcome.Superseded>(await ConfirmAsync(user, CodeFor(start)));
    }

    // G3.25
    [Fact]
    public async Task TheFifthWrongConfirm_EndsTheEnrolment()
    {
        var user = await SeedAsync();
        var start = await StartAsync(user);

        var left = new List<int>();
        for (var i = 0; i < 5; i++)
            left.Add(Assert.IsType<ConfirmOutcome.Rejected>(await ConfirmAsync(user, WrongCodeFor(start))).AttemptsLeft);

        Assert.Equal([4, 3, 2, 1, 0], left);
        Assert.Empty(await PendingValuesAsync(user));
        Assert.IsType<ConfirmOutcome.Superseded>(await ConfirmAsync(user, CodeFor(start)));
        Assert.Equal(0, (await UserAsync(user)).AccessFailedCount);
    }

    // G3.26
    [Fact]
    public async Task AnUnreadablePayload_IsDeleted_AndConfirmIsRefusedUncharged()
    {
        var user = await SeedAsync();
        var start = await StartAsync(user);
        var foreign = new EphemeralDataProtectionProvider().CreateProtector("another-ring").Protect("{}");
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserTokens
                .Where(t => t.UserId == user.UserId && t.Name == "PendingAuthenticator")
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Value, foreign));
        factory.Sink.Events.Clear();

        Assert.IsType<ConfirmOutcome.Superseded>(await ConfirmAsync(user, CodeFor(start)));
        Assert.Empty(await PendingValuesAsync(user));
        Assert.Equal(0, (await UserAsync(user)).AccessFailedCount);
        Assert.Single(factory.EventsFor(SecurityEvents.SecondFactorKeyUnreadable));
    }

    // A payload that decrypts to JSON null or has no key ends the enrolment, as
    // an undecryptable one does, never a 500.
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task AFieldlessPayload_IsDeleted_AndConfirmIsRefused(string json)
    {
        var user = await SeedAsync();
        var start = await StartAsync(user);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(user.AccountId);
            await scope.ServiceProvider.GetRequiredService<TwoFactorCredentials>()
                .StageProtectedAsync(user.UserId, ProtectedTwoFactorValue.PendingAuthenticator, json, default);
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync();
        }

        Assert.IsType<ConfirmOutcome.Superseded>(await ConfirmAsync(user, CodeFor(start)));
        Assert.Empty(await PendingValuesAsync(user));
    }

    // G3.28: the lock reloads the row, which would discard the caller's change.
    public static TheoryData<string> WriteMethods => ["VerifyAsync", "StartEnrolmentAsync", "ConfirmEnrolmentAsync"];

    [Theory]
    [MemberData(nameof(WriteMethods))]
    public async Task AWriteMethod_WithTheUserModified_Throws(string method)
    {
        var user = await SeedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => InTenantAsync(user, async (credentials, db, ct) =>
        {
            (await db.Users.SingleAsync(u => u.Id == user.UserId, ct)).DisplayName = "Unsaved";
            return method switch
            {
                "VerifyAsync" => (object)await credentials.VerifyAsync(
                    user.AccountId, user.UserId, new SecondFactorCode.Totp(0), new ProofBinding(1, "", null), ct),
                "StartEnrolmentAsync" => await credentials.StartEnrolmentAsync(user.AccountId, user.UserId, ct),
                _ => await credentials.ConfirmEnrolmentAsync(user.AccountId, user.UserId, new SecondFactorCode.Totp(0), ct),
            };
        }));
    }
}
