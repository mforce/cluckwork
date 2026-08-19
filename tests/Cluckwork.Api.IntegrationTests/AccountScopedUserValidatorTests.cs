namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #532 — the validator runs on Identity's UPDATE pipeline, not only on create.
// AccountLockout.RecordFailedAccessAsync reads a failed IdentityResult as a lost
// concurrency race: it reloads and retries ten times, then returns false. So a
// validator that rejects an ordinary PERSISTED user makes AccessFailedCount stop
// incrementing and the #128 account lockout goes silently inert — with login
// still answering its ordinary failure, so nothing looks wrong.
//
// RequireUniqueEmail is newly true in this slice, which makes the email branch
// live for the first time over rows nothing ever validated: EmailIndex was
// non-unique on main and the stock email checks never ran. These tests pin that
// such a row still increments its counter.
[Collection(IntegrationCollection.Name)]
public sealed class AccountScopedUserValidatorTests(CluckworkWebApplicationFactory factory)
{
    // Writes a value Identity would refuse, WITHOUT going through Identity —
    // exactly how a legacy row comes to exist.
    private Task CorruptStoredEmailAsync(Guid accountId, string email, string corrupted) =>
        factory.WithTenantScopeAsync(accountId, db => db.Users
            .Where(u => u.Email == email)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Email, corrupted)
                .SetProperty(u => u.NormalizedEmail, corrupted.ToUpperInvariant())));

    private async Task<(IdentityResult Result, int Count)> AccessFailedOnceAsync(string originalEmail)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.UserName == originalEmail);

        var result = await users.AccessFailedAsync(user);
        var count = await db.Users.AsNoTracking()
            .Where(u => u.Id == user.Id).Select(u => u.AccessFailedCount).SingleAsync();
        return (result, count);
    }

    // NULL is deliberately NOT among these: RequireUserIdentityColumns makes
    // Email/NormalizedEmail/UserName/NormalizedUserName NOT NULL, so the database
    // now refuses it and a case for it could never run. A MALFORMED address is
    // still perfectly storable, which is what these cover.
    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    public async Task ALegacyRowWithAnUnvalidatedEmail_StillIncrementsItsLockoutCounter(string corrupted)
    {
        var email = $"legacy-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        await CorruptStoredEmailAsync(accountId, email, corrupted);

        var (result, count) = await AccessFailedOnceAsync(email);

        Assert.True(result.Succeeded,
            "a persisted row the validator dislikes must not fail the update pipeline — "
            + "AccountLockout reads that as a concurrency race and the #128 lockout goes inert: "
            + string.Join("; ", result.Errors.Select(e => $"{e.Code}:{e.Description}")));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ChangingAnEmailToAMalformedValue_IsStillRejected()
    {
        // The short-circuit must not become a hole: it skips only values that
        // are ALREADY persisted. A caller supplying a new malformed address is
        // changing the value, so full validation applies.
        var email = $"change-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.UserName == email);

        var result = await users.SetEmailAsync(user, "definitely not an email");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code.Contains("Email", StringComparison.Ordinal));
    }
}
