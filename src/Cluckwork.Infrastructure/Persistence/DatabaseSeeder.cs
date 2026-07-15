namespace Cluckwork.Infrastructure.Persistence;

using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Idempotent single-farm seed: the default account, an "Admin" role, and one
// admin user. Runs at startup (see Program). Credentials come only from
// configuration — no fallback secret is ever invented.
//
// Seeding is best-effort: any failure (weak password, an email owned by another
// account, a concurrent insert from another replica) is logged and skipped
// rather than crashing the host, so the app still starts. A multi-replica
// deployment that needs a hard guarantee should add a distributed lock
// (e.g. pg_advisory_lock) around startup — deferred until that is a real need.
public sealed class DatabaseSeeder(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    RoleManager<ApplicationRole> roles,
    IOptions<SeedOptions> options,
    ILogger<DatabaseSeeder> logger)
{
    public const string AdminRole = "Admin";

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var o = options.Value;

        if (!o.Enabled)
        {
            logger.LogInformation("Seed disabled (Seed:Enabled=false); skipping.");
            return;
        }

        if (string.IsNullOrWhiteSpace(o.AdminEmail) || string.IsNullOrWhiteSpace(o.AdminPassword))
        {
            logger.LogWarning(
                "Seed enabled but Seed:AdminEmail / Seed:AdminPassword are not set; skipping seed. " +
                "No default credentials are created.");
            return;
        }

        await SeedDefaultAccountAsync(o, ct);
        await SeedAdminRoleAsync();
        await SeedAdminUserAsync(o);
    }

    private async Task SeedDefaultAccountAsync(SeedOptions o, CancellationToken ct)
    {
        // TenantContext is unresolved at startup, so the account query filter would
        // hide existing rows — bypass it for the existence check.
        var exists = await db.Accounts
            .IgnoreQueryFilters()
            .AnyAsync(a => a.Id == SeedDefaults.AccountId, ct);

        if (exists) return;

        db.Accounts.Add(Account.Create(SeedDefaults.AccountId, o.AccountName, "UTC", "USD"));
        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded default account {AccountId}.", SeedDefaults.AccountId);
        }
        catch (DbUpdateException)
        {
            // Lost a race with another replica inserting the same fixed PK — the
            // account now exists, which is all we wanted. Detach the failed insert
            // so later SaveChanges on this context don't retry it.
            var pending = db.Accounts.Local.FirstOrDefault(a => a.Id == SeedDefaults.AccountId);
            if (pending is not null) db.Entry(pending).State = EntityState.Detached;
            logger.LogInformation("Default account already present (concurrent insert); continuing.");
        }
    }

    private async Task SeedAdminRoleAsync()
    {
        if (await roles.RoleExistsAsync(AdminRole)) return;

        var result = await roles.CreateAsync(new ApplicationRole { Id = Guid.NewGuid(), Name = AdminRole });
        if (result.Succeeded)
            logger.LogInformation("Seeded {Role} role.", AdminRole);
        else
            logger.LogError("Failed to seed {Role} role: {Errors}", AdminRole, Describe(result));
    }

    private async Task SeedAdminUserAsync(SeedOptions o)
    {
        var existing = await users.FindByEmailAsync(o.AdminEmail);
        if (existing is not null)
        {
            if (existing.AccountId != SeedDefaults.AccountId)
            {
                // Config conflict: the email already belongs to a user of a
                // different account. Don't hijack it and don't crash — surface it.
                logger.LogError(
                    "Seed:AdminEmail {Email} already belongs to account {OtherAccount}, not the default " +
                    "account {DefaultAccount}. Skipping admin seed — the default account has no admin.",
                    o.AdminEmail, existing.AccountId, SeedDefaults.AccountId);
                return;
            }

            await EnsureInAdminRoleAsync(existing);
            return;
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = o.AdminEmail,
            Email = o.AdminEmail,
            EmailConfirmed = true,
            AccountId = SeedDefaults.AccountId,
            DisplayName = "Administrator",
        };

        var result = await users.CreateAsync(user, o.AdminPassword);
        if (!result.Succeeded)
        {
            // Weak password, or a concurrent replica already created this email.
            // Log and skip rather than crash the host.
            logger.LogError("Failed to seed admin user {Email}: {Errors}", o.AdminEmail, Describe(result));
            return;
        }

        await EnsureInAdminRoleAsync(user);
        logger.LogInformation("Seeded admin user {Email}.", o.AdminEmail);
    }

    private async Task EnsureInAdminRoleAsync(ApplicationUser user)
    {
        if (await users.IsInRoleAsync(user, AdminRole)) return;
        var result = await users.AddToRoleAsync(user, AdminRole);
        if (!result.Succeeded)
            logger.LogError("Failed to add {Email} to {Role}: {Errors}", user.Email, AdminRole, Describe(result));
    }

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => e.Description));
}
