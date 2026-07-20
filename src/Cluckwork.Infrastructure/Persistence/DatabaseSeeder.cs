namespace Cluckwork.Infrastructure.Persistence;

using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Eggs;
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
        await SeedDefaultEggGradesAsync(ct);
        await SeedDefaultEggUnitConversionsAsync(ct);
        await SeedAdminRoleAsync();
        await SeedAdminUserAsync(o);
        await SeedWorkerUserAsync(o);
    }

    // Spec §9.1 suggested defaults. Saleable grades are what daily-entry grade
    // lines may reference; the non-saleable buckets exist for future use (the
    // entry's cracked/dirty/discarded counts cover losses in the MVP).
    private static readonly (string Name, EggGradeType Type, bool Saleable)[] DefaultGrades =
    [
        ("Small", EggGradeType.Size, true),
        ("Medium", EggGradeType.Size, true),
        ("Large", EggGradeType.Size, true),
        ("Jumbo", EggGradeType.Size, true),
        ("Seconds", EggGradeType.Quality, true),
        ("Cracked", EggGradeType.Quality, false),
        ("Dirty", EggGradeType.Quality, false),
        ("Soft Shell", EggGradeType.Quality, false),
        ("Discarded", EggGradeType.Custom, false),
        ("Internal Use", EggGradeType.Custom, false),
    ];

    private async Task SeedDefaultEggGradesAsync(CancellationToken ct)
    {
        // Defaults are seeded only into an empty catalog. Once any grade exists
        // the catalog is user-managed (#42): re-seeding by name would resurrect
        // renamed or deliberately removed defaults on every startup.
        var anyGrades = await db.EggGrades
            .IgnoreQueryFilters()
            .AnyAsync(g => g.AccountId == SeedDefaults.AccountId, ct);
        if (anyGrades) return;

        var missing = DefaultGrades
            .Select((d, index) => EggGrade.Create(
                Guid.NewGuid(), SeedDefaults.AccountId, SeedDefaults.FarmId,
                d.Name, d.Type,
                sortOrder: index,
                isSaleable: d.Saleable))
            .ToList();

        db.EggGrades.AddRange(missing);
        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded {Count} default egg grades.", missing.Count);
        }
        catch (DbUpdateException ex)
        {
            foreach (var grade in missing)
                db.Entry(grade).State = EntityState.Detached;

            if (IsUniqueViolation(ex))
                // Concurrent replica seeded the same names — they exist, which is
                // all we wanted.
                logger.LogInformation("Default egg grades already present (concurrent insert); continuing.");
            else
                // Genuine failure: the tenant is left without default grades.
                // Startup stays best-effort, but this must be loud, not "healthy".
                logger.LogError(ex, "Failed to seed default egg grades.");
        }
    }

    // Spec §9.7 packed-unit defaults (#97). Same only-into-an-empty-catalog rule
    // as grades: once any row exists the conversions are user-managed, and
    // re-seeding would resurrect deliberately deactivated units.
    private async Task SeedDefaultEggUnitConversionsAsync(CancellationToken ct)
    {
        var any = await db.EggUnitConversions
            .IgnoreQueryFilters()
            .AnyAsync(c => c.AccountId == SeedDefaults.AccountId, ct);
        if (any) return;

        var defaults = Cluckwork.Domain.Catalog.EggUnitConversion.Defaults(SeedDefaults.AccountId);
        db.EggUnitConversions.AddRange(defaults);
        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded {Count} default egg unit conversions.", defaults.Count);
        }
        catch (DbUpdateException ex)
        {
            foreach (var row in defaults)
                db.Entry(row).State = EntityState.Detached;

            if (IsUniqueViolation(ex))
                logger.LogInformation("Default egg unit conversions already present (concurrent insert); continuing.");
            else
                logger.LogError(ex, "Failed to seed default egg unit conversions.");
        }
    }

    // Postgres unique_violation. Other DbUpdateExceptions (connection loss, batch
    // errors, ...) are real failures and must not be mistaken for "already seeded".
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

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
        catch (DbUpdateException ex)
        {
            // Detach the failed insert so later SaveChanges on this context don't
            // retry it.
            var pending = db.Accounts.Local.FirstOrDefault(a => a.Id == SeedDefaults.AccountId);
            if (pending is not null) db.Entry(pending).State = EntityState.Detached;

            if (IsUniqueViolation(ex))
                // Lost a race with another replica inserting the same fixed PK —
                // the account now exists, which is all we wanted.
                logger.LogInformation("Default account already present (concurrent insert); continuing.");
            else
                logger.LogError(ex, "Failed to seed the default account.");
        }
    }

    // #103: every assignable role exists up front so role assignment never
    // races role creation.
    private async Task SeedAdminRoleAsync()
    {
        foreach (var name in Cluckwork.Domain.Accounts.Roles.Assignable)
        {
            if (await roles.RoleExistsAsync(name)) continue;
            var result = await roles.CreateAsync(new ApplicationRole { Id = Guid.NewGuid(), Name = name });
            if (result.Succeeded)
                logger.LogInformation("Seeded {Role} role.", name);
            else
                logger.LogError("Failed to seed {Role} role: {Errors}", name, Describe(result));
        }
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

    // Optional non-admin login (#73): created without any role so the worker
    // experience is testable out of the box. Skipped unless BOTH Seed:WorkerEmail
    // and Seed:WorkerPassword are supplied — never a fallback credential. An
    // existing user's roles are left untouched (a later promotion must not be
    // silently reverted on restart).
    private async Task SeedWorkerUserAsync(SeedOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.WorkerEmail) || string.IsNullOrWhiteSpace(o.WorkerPassword))
            return;

        var existing = await users.FindByEmailAsync(o.WorkerEmail);
        if (existing is not null)
        {
            if (existing.AccountId != SeedDefaults.AccountId)
                logger.LogError(
                    "Seed:WorkerEmail {Email} already belongs to account {OtherAccount}, not the default " +
                    "account {DefaultAccount}. Skipping worker seed.",
                    o.WorkerEmail, existing.AccountId, SeedDefaults.AccountId);
            return;
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = o.WorkerEmail,
            Email = o.WorkerEmail,
            EmailConfirmed = true,
            AccountId = SeedDefaults.AccountId,
            DisplayName = "Worker",
        };

        var result = await users.CreateAsync(user, o.WorkerPassword);
        if (result.Succeeded)
            logger.LogInformation("Seeded worker user {Email}.", o.WorkerEmail);
        else
            logger.LogError("Failed to seed worker user {Email}: {Errors}", o.WorkerEmail, Describe(result));
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
