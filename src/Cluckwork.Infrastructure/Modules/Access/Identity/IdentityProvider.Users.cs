using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Commerce.Contracts;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

public sealed partial class IdentityProvider
{
    public Task<Result<Guid>> CreateUserAsync(
        Guid accountId, string email, string password, string? role,
        string? name = null, bool mustChangePassword = false, CancellationToken ct = default) =>
        // One transaction around create + role assignment: a failed admin
        // creation must not survive as a usable role-less worker account
        // (codex review of PR #78). Disposal without commit rolls back.
        // #307 — joins IdempotencyMiddleware's ambient request transaction
        // when one is open, instead of nesting a second one.
        //
        // #269 — the delegate shape (rather than a scope the caller drives)
        // is what EnableRetryOnFailure forces: a user-initiated transaction
        // has to be opened inside an execution strategy. It is NOT retried —
        // AmbientTransaction's owned path runs it exactly once. Retrying it
        // would be actively wrong: a failed userManager.CreateAsync leaves
        // its ApplicationUser tracked as Added (EF does not detach it, and
        // minting a fresh Guid for the retry does not either), so a second
        // attempt flushes BOTH users into the unique email index and reports
        // a duplicate-key failure in place of the connection failure that
        // actually happened (#269 review). A blanket db.ChangeTracker.Clear()
        // is NOT the alternative — on the CLI/owned path this same
        // AppDbContext can be shared by a longer-lived caller (e.g.
        // SimulationDataSeeder) holding its own entities tracked across
        // several handler calls, and clearing silently drops that caller's
        // pending SaveChanges.
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                AccountId = accountId,
                DisplayName = name, // #163 — optional display name at creation
                MustChangePassword = mustChangePassword // generated offline-provisioning credential
            };

            var created = await userManager.CreateAsync(user, password);
            if (!created.Succeeded)
                return Result.Failure<Guid>(await CreateFailureAsync(created, email, accountId, token));

            if (role is not null)
            {
                if (!Cluckwork.Domain.Modules.Farm.Contracts.Roles.Assignable.Contains(role))
                    return Result.Failure<Guid>(Error.Validation(
                        "Users.UnknownRole", $"'{role}' is not an assignable role."));

                if (!await roleManager.RoleExistsAsync(role))
                {
                    var roleCreated = await roleManager.CreateAsync(
                        new ApplicationRole { Id = Guid.NewGuid(), Name = role });
                    if (!roleCreated.Succeeded)
                        return Result.Failure<Guid>(Error.Validation("Users.CreateFailed", Describe(roleCreated)));
                }

                var addedToRole = await userManager.AddToRoleAsync(user, role);
                if (!addedToRole.Succeeded)
                    return Result.Failure<Guid>(Error.Validation("Users.CreateFailed", Describe(addedToRole)));
            }

            // Same transaction as the creation (#93): the event needs its own
            // SaveChanges because UserManager flushed its writes already.
            await audit.WriteAsync(AuditActions.UserCreate, "User", user.Id,
                reason: null, details: new { email, role = role ?? "Worker" }, ct: token);
            await db.SaveChangesAsync(token);

            await transaction.CommitAsync(token);
            return Result.Success(user.Id);
        }, ct);

    public async Task<Result> UpdateUserAsync(
        Guid accountId, Guid userId, string? name, CancellationToken ct = default)
    {
        // Scoped to the account: a user id from another tenant simply doesn't
        // match, so this returns NotFound rather than editing a foreign user.
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Id == userId && u.AccountId == accountId, ct);
        if (user is null)
            return Result.Failure(Error.NotFound("Users", userId));

        user.DisplayName = name;
        // Rotate Identity's concurrency token so two concurrent edits don't
        // silently last-write-win: EF checks the ORIGINAL stamp in the UPDATE's
        // WHERE, so the loser matches no row and fails closed to a 409.
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        await audit.WriteAsync(AuditActions.UserUpdate, "User", user.Id,
            reason: null, details: new { name }, ct: ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(Error.Conflict(
                "Users.Conflict", "The user was modified by another request. Reload and retry."));
        }
        return Result.Success();
    }

    // Identity's duplicate-email wording is only surfaced when the email
    // already belongs to THIS account. A duplicate in another tenant gets a
    // generic message so the endpoint is not a cross-tenant registration
    // oracle — which is load-bearing since #530, because one address can
    // legitimately belong to users in several farms.
    private async Task<Error> CreateFailureAsync(
        IdentityResult result, string email, Guid accountId, CancellationToken ct)
    {
        if (!result.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail"))
            return Error.Validation("Users.CreateFailed", Describe(result));

        // #532 — scoped, and it HAD to be: this runs on the duplicate path, i.e.
        // exactly when duplicates exist, and the global lookup it replaces
        // (SingleOrDefaultAsync) threw there — turning a clean 400 into a 500.
        // The account comparison that used to follow is now redundant, because
        // the lookup itself cannot return another farm's row.
        var existing = await directory.FindByAccountEmailAsync(accountId, email, ct);
        return existing is not null
            ? Error.Validation("Users.DuplicateEmail", "A user with this email already exists.")
            : Error.Validation("Users.CreateFailed", "Could not create the user.");
    }

    public async Task<IReadOnlyList<UserSummary>> ListUsersAsync(Guid accountId, CancellationToken ct = default)
    {
        var roleByUser = await (
            from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            select new { userRole.UserId, role.Name })
            .ToListAsync(ct);
        // Highest role wins, matching AuthPolicies.EffectiveRole — an
        // Owner+ReadOnly user must list as Owner, not by insertion order
        // (codex/conventions review of #104).
        var lookup = roleByUser
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => Rank(x.Name)).First().Name!);

        // #356 — disabled users are LISTED, not filtered out: an Owner cannot
        // re-enable someone the list refuses to show them.
        var rows = await db.Users
            .Where(u => u.AccountId == accountId)
            .OrderBy(u => u.Email)
            .Select(u => new { u.Id, u.Email, u.DisplayName, u.DisabledAt })
            .ToListAsync(ct);
        return rows
            .Select(u => new UserSummary(
                u.Id, u.Email!, u.DisplayName, lookup.GetValueOrDefault(u.Id, "Worker"), u.DisabledAt))
            .ToList();
    }

    // Highest role wins, matching AuthPolicies.EffectiveRole — an Owner+ReadOnly
    // user resolves to Owner, not by insertion order.
    private static int Rank(string? name) => name switch
    {
        Cluckwork.Domain.Modules.Farm.Contracts.Roles.Owner => 4,
        Cluckwork.Domain.Modules.Farm.Contracts.Roles.Manager => 3,
        Cluckwork.Domain.Modules.Farm.Contracts.Roles.Sales => 2,
        Cluckwork.Domain.Modules.Farm.Contracts.Roles.ReadOnly => 1,
        _ => 0,
    };

    public async Task<UserProfile?> GetUserAsync(
        Guid accountId, Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users
            .Where(u => u.Id == userId && u.AccountId == accountId)
            .Select(u => new { u.Id, u.Email, u.DisplayName, u.Language, u.PreferredStepperUnit })
            .FirstOrDefaultAsync(ct);
        if (user is null) return null;

        var roleNames = await (
            from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == userId
            select role.Name).ToListAsync(ct);
        var effectiveRole = roleNames.OrderByDescending(Rank).FirstOrDefault() ?? "Worker";

        return new UserProfile(
            user.Id, user.Email!, user.DisplayName, effectiveRole, user.Language,
            user.PreferredStepperUnit);
    }

    public async Task<Result> SetLanguageAsync(
        Guid accountId, Guid userId, string? language, CancellationToken ct = default)
    {
        var affected = await db.Users
            .Where(u => u.Id == userId && u.AccountId == accountId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, language), ct);
        return affected == 0 ? Result.Failure(Error.NotFound("Users", userId)) : Result.Success();
    }

    // #444 — same shape as SetLanguageAsync; the caller has already confirmed a
    // non-null unit is still an active EggUnitConversion.
    public async Task<Result> SetStepperUnitAsync(
        Guid accountId, Guid userId, EggUnit? unit, CancellationToken ct = default)
    {
        var affected = await db.Users
            .Where(u => u.Id == userId && u.AccountId == accountId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PreferredStepperUnit, unit), ct);
        return affected == 0 ? Result.Failure(Error.NotFound("Users", userId)) : Result.Success();
    }
}
