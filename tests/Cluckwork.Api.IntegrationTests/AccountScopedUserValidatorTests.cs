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

    // Sibling of CorruptStoredEmailAsync: corrupts the user-name pair instead, so
    // the USERNAME half of the validator's unchanged-value short-circuit is
    // guarded the same way the email half is.
    private Task CorruptStoredUserNameAsync(Guid accountId, Guid userId, string corrupted) =>
        factory.WithTenantScopeAsync(accountId, db => db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.UserName, corrupted)
                .SetProperty(u => u.NormalizedUserName, corrupted.ToUpperInvariant())));

    private async Task<(IdentityResult Result, int Count)> AccessFailedOnceAsync(
        string originalEmail, Guid? userId = null)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // By id where given (the corruption may have set UserName itself),
        // by the original email otherwise (UserName == email is the app's
        // invariant, and the email case never touches UserName).
        var user = userId is null
            ? await db.Users.SingleAsync(u => u.UserName == originalEmail)
            : await db.Users.SingleAsync(u => u.Id == userId);

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

    [Theory]
    [InlineData("legacy user")]
    public async Task ALegacyRowWithAnUnvalidatedUserName_StillIncrementsItsLockoutCounter(string corrupted)
    {
        // A space is outside Identity's default AllowedUserNameCharacters, so a
        // validator that re-litigated the stored value would fail the update
        // pipeline exactly as the email case does. Deleting the username
        // short-circuit must redden this test.
        var email = $"legacyuser-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);

        // The corruption sets UserName itself, so both it and the lookup below
        // must address the user by id, not by name (AccessFailedOnceAsync
        // finds it by UserName == originalEmail only because the email case
        // never touches UserName).
        var userId = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.Email == email).Select(u => u.Id).FirstAsync());
        await CorruptStoredUserNameAsync(accountId, userId, corrupted);

        var (result, count) = await AccessFailedOnceAsync(email, userId);

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

    [Fact]
    public async Task RenamingToANormalizationEquivalentValue_IsStillValidated()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"raw-{Guid.NewGuid():N}@test.local");
        var email = $"s{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, asAdmin: false);

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);

        // U+017F upper-invariants to 'S', so this normalizes to the SAME value the
        // row already holds. Compare normalized and the short-circuit fires,
        // AllowedUserNameCharacters never runs, and a value stock Identity refuses
        // gets persisted. Compare raw — which is the fix — and it is validated.
        var collision = "ſ" + email[1..];
        var result = await users.SetUserNameAsync(user!, collision);
        Assert.False(result.Succeeded,
            "a rename that merely normalizes to the stored value must still be validated");
    }
}
