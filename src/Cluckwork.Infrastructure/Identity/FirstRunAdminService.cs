namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

// #283 Part 2 — first-run admin provisioning. Invoked ONLY by the
// `bootstrap-admin` CLI command (Cli/BootstrapAdminCliCommand.cs), a one-shot
// operator command, never an HTTP endpoint and never a serving-boot side
// effect (mirrors AdminRecoveryService's #265 shape: a thin CLI wrapper
// handles args/stdout/exit-codes, this does the real work).
//
// The default account/Admin role/default egg grades are #283 Part 1 static
// reference data baked into the EF migrations (InsertData) — always present
// once the schema is current. NO user row is ever baked into a migration
// (that would ship every deployment the same publicly-known credential): the
// first Owner is created HERE, on first run, with a freshly generated
// password nothing but this one process ever sees.
//
// Idempotent (#283 requirement): a re-run against an already-provisioned
// account (an Owner already exists) is a clean no-op success — never a
// duplicate Owner, never a second printed secret.
public sealed class FirstRunAdminService(
    AppDbContext db,
    TenantContext tenant,
    UserManager<ApplicationUser> userManager,
    IIdentityProvider identity)
{
    public async Task<Result<FirstRunAdminOutcome>> ProvisionAsync(
        string? email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return Result.Failure<FirstRunAdminOutcome>(Error.Validation(
                "Bootstrap.EmailRequired", "An --email is required."));

        var accountId = SeedDefaults.AccountId;

        // The account itself is migration-baked and should always exist once
        // the schema is current (MigrateAsync above already ran) — this is
        // defense-in-depth against a hand-rolled/partially-restored schema,
        // not the expected path.
        var accountExists = await db.Accounts
            .IgnoreQueryFilters()
            .AnyAsync(a => a.Id == accountId, ct);
        if (!accountExists)
            return Result.Failure<FirstRunAdminOutcome>(Error.Validation(
                "Bootstrap.AccountMissing",
                "The default account does not exist. This should never happen against a schema this " +
                "command's own migrate step just brought current — check the migration history."));

        // Idempotency: an Owner already existing in the default account means
        // first-run provisioning already happened.
        var owners = await userManager.GetUsersInRoleAsync(Roles.Owner);
        if (owners.Any(u => u.AccountId == accountId))
            return Result.Success(FirstRunAdminOutcome.AlreadyProvisioned());

        // Conflict check BEFORE mutating anything, same shape as
        // DatabaseSeeder's old cross-account guard: don't hijack an existing
        // email and don't crash — a clear fail-loud message instead.
        var normalizedEmail = userManager.NormalizeEmail(email.Trim());
        var conflicting = await db.Users
            .Where(u => u.NormalizedEmail == normalizedEmail)
            .Select(u => new { u.AccountId })
            .FirstOrDefaultAsync(ct);
        if (conflicting is not null)
            return Result.Failure<FirstRunAdminOutcome>(Error.Validation(
                "Bootstrap.EmailInUse",
                conflicting.AccountId == accountId
                    ? $"A user with email '{email.Trim()}' already exists in the default account but holds " +
                      "no Owner role. Assign the Admin role via the Users page, or choose a different --email."
                    : $"A user with email '{email.Trim()}' already exists under a different account."));

        // Handlers/audit need the tenant, which is unresolved outside an HTTP
        // request — resolve it to the default account for this scope (mirrors
        // AdminRecoveryService and the demo/simulation seeders).
        tenant.Resolve(accountId);

        var password = TemporaryPassword.Generate();
        var created = await identity.CreateUserAsync(
            accountId, email.Trim(), password, Roles.Owner,
            name: "Administrator", mustChangePassword: true, ct: ct);
        if (created.IsFailure)
            return Result.Failure<FirstRunAdminOutcome>(created.Error);

        return Result.Success(FirstRunAdminOutcome.Provisioned(email.Trim(), accountId, password));
    }
}

public sealed record FirstRunAdminOutcome(
    bool WasAlreadyProvisioned, string? Email, Guid? AccountId, string? TemporaryPassword)
{
    public static FirstRunAdminOutcome AlreadyProvisioned() => new(true, null, null, null);

    public static FirstRunAdminOutcome Provisioned(string email, Guid accountId, string password) =>
        new(false, email, accountId, password);
}
