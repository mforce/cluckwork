namespace Cluckwork.Infrastructure.Persistence;

using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Idempotent single-farm seed: the default account + one admin user. Runs at
// startup (see Program). Credentials come only from configuration — no fallback
// secret is ever invented.
public sealed class DatabaseSeeder(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    IOptions<SeedOptions> options,
    ILogger<DatabaseSeeder> logger)
{
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
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded default account {AccountId}.", SeedDefaults.AccountId);
    }

    private async Task SeedAdminUserAsync(SeedOptions o)
    {
        if (await users.FindByEmailAsync(o.AdminEmail) is not null) return;

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
            throw new InvalidOperationException(
                "Failed to seed admin user: " +
                string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        logger.LogInformation("Seeded admin user {Email}.", o.AdminEmail);
    }
}
