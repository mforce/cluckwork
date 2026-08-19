namespace Cluckwork.Infrastructure.Identity;

using System.ComponentModel.DataAnnotations;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #532 — REPLACES Identity's stock UserValidator<ApplicationUser>. It does not
// run beside it, and that distinction is the whole point: UserManager runs
// EVERY registered IUserValidator, and the stock one always performs a GLOBAL
// FindByNameAsync. Farm B's copy of an email Farm A already uses would be
// rejected as DuplicateUserName before Postgres ever evaluated the composite
// (AccountId, NormalizedUserName) index. Registration ORDER is what displaces
// it: this type is registered before AddIdentityCore, whose TryAddScoped then
// no-ops. AccountScopedUserValidatorRegistrationTests pins that there is
// exactly one validator and that it is this one.
//
// Everything the stock validator did is reimplemented below, because dropping
// any of it is SILENT — the pipeline simply stops enforcing it:
//   * non-blank user name, and Options.User.AllowedUserNameCharacters;
//   * normalization through the manager (CreateAsync validates BEFORE
//     NormalizedUserName/NormalizedEmail are written to the entity, so the
//     lookups here must normalize the incoming values themselves);
//   * required + well-formed email, honouring Options.User.RequireUniqueEmail
//     exactly as stock does;
//   * duplicate detection scoped to the user's own account and EXCLUDING the
//     user itself.
//
// The self-exclusion is the one to be careful with. This validator also runs on
// the UPDATE pipeline (AccessFailedAsync, ResetPasswordAsync, ChangePasswordAsync,
// AddToRoleAsync, security-stamp rotation), and AccountLockout.RecordFailedAccessAsync
// reads a failed IdentityResult as a lost concurrency race: it reloads and retries
// ten times, then returns false. So a validator that rejects an ordinary persisted
// user makes AccessFailedCount never increment — the #128 account lockout goes
// silently inert while login still returns 200.
//
// The DATABASE indexes remain the authoritative concurrency guard. This type only
// turns a would-be raw DbUpdateException into a clean IdentityResult.
internal sealed class AccountScopedUserValidator(
    AppDbContext db, IdentityErrorDescriber describer) : IUserValidator<ApplicationUser>
{
    public async Task<IdentityResult> ValidateAsync(
        UserManager<ApplicationUser> manager, ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(user);

        var errors = new List<IdentityError>();

        // An account-less user would otherwise collapse into a single pseudo-account
        // with every other account-less user, and the scoped queries below would read
        // their collisions as legitimate distinct-farm duplicates.
        if (user.AccountId == Guid.Empty)
        {
            errors.Add(new IdentityError
            {
                Code = "UserWithoutAccount",
                Description = "A user must belong to a farm account."
            });
        }

        await ValidateUserNameAsync(manager, user, errors);
        await ValidateEmailAsync(manager, user, errors);

        return errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errors]);
    }

    private async Task ValidateUserNameAsync(
        UserManager<ApplicationUser> manager, ApplicationUser user, List<IdentityError> errors)
    {
        var userName = await manager.GetUserNameAsync(user);
        if (string.IsNullOrWhiteSpace(userName))
        {
            errors.Add(describer.InvalidUserName(userName));
            return;
        }

        var allowed = manager.Options.User.AllowedUserNameCharacters;
        if (!string.IsNullOrEmpty(allowed) && userName.Any(c => !allowed.Contains(c)))
        {
            errors.Add(describer.InvalidUserName(userName));
            return;
        }

        // NormalizeName, not the entity's NormalizedUserName: on CreateAsync the
        // entity's normalized columns are still null at validation time.
        var normalized = manager.NormalizeName(userName);
        var taken = await db.Users.AsNoTracking().AnyAsync(u =>
            u.AccountId == user.AccountId
            && u.NormalizedUserName == normalized
            && u.Id != user.Id);
        if (taken)
            errors.Add(describer.DuplicateUserName(userName));
    }

    private async Task ValidateEmailAsync(
        UserManager<ApplicationUser> manager, ApplicationUser user, List<IdentityError> errors)
    {
        // Stock only inspects the email when RequireUniqueEmail is set; mirroring
        // that keeps this a replacement rather than a new policy. AddCluckworkIdentity
        // sets it true explicitly, so the branch is live.
        if (!manager.Options.User.RequireUniqueEmail) return;

        var email = await manager.GetEmailAsync(user);
        if (string.IsNullOrWhiteSpace(email))
        {
            errors.Add(describer.InvalidEmail(email));
            return;
        }

        if (!new EmailAddressAttribute().IsValid(email))
        {
            errors.Add(describer.InvalidEmail(email));
            return;
        }

        var normalized = manager.NormalizeEmail(email);
        var taken = await db.Users.AsNoTracking().AnyAsync(u =>
            u.AccountId == user.AccountId
            && u.NormalizedEmail == normalized
            && u.Id != user.Id);
        if (taken)
            errors.Add(describer.DuplicateEmail(email));
    }
}

// The validator is internal (it takes AppDbContext), so the Api layer registers
// it through this extension rather than naming the type — same shape as
// PersistentStepUpGrantRegistryRegistration.
public static class AccountScopedUserValidatorRegistration
{
    public static IServiceCollection AddAccountScopedUserValidator(this IServiceCollection services)
    {
        services.AddScoped<IUserValidator<ApplicationUser>, AccountScopedUserValidator>();
        return services;
    }
}
