namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

// #265 — offline break-glass account recovery. Motivating case: a single-Owner
// farm loses its password and there is no email/SMTP reset path, so the only
// other recourse is direct DB surgery. Invoked by the `recover-admin` CLI
// command (Program.cs), never an HTTP endpoint.
//
// UNLIKE the demo/simulation seeders this is deliberately NOT environment-gated:
// break-glass must work against a real Production database — that is the whole
// point. Its safety comes from requiring shell access to the running deployment
// (to invoke the command at all), and from writing a conspicuous audit row.
public sealed class AdminRecoveryService(
    AppDbContext db,
    TenantContext tenant,
    UserManager<ApplicationUser> userManager,
    IIdentityProvider identity)
{
    public async Task<Result<AdminRecoveryResult>> RecoverAsync(
        string? email, Guid? accountId, string? reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return Result.Failure<AdminRecoveryResult>(Error.Validation(
                "Recovery.EmailRequired", "An --email is required."));

        // Match on NormalizedEmail, exactly like Identity's login path
        // (FindByEmailAsync) — a case-sensitive `Email ==` compare would miss an
        // account stored as `Owner@Farm.example` when the operator types
        // `owner@farm.example`, returning a spurious NotFound in the one moment
        // the tool must not (#265 review). db.Users carries no tenant query
        // filter, so this works before any tenant is resolved.
        var normalized = email.Trim();
        var normalizedEmail = userManager.NormalizeEmail(normalized);
        var matches = await db.Users
            .Where(u => u.NormalizedEmail == normalizedEmail && (accountId == null || u.AccountId == accountId))
            .Select(u => new { u.Id, u.AccountId, u.Email })
            .ToListAsync(ct);

        if (matches.Count == 0)
            return Result.Failure<AdminRecoveryResult>(Error.NotFound("Recovery", normalized));
        if (matches.Count > 1)
            return Result.Failure<AdminRecoveryResult>(Error.Validation(
                "Recovery.Ambiguous",
                $"{matches.Count} users share the email '{normalized}' across accounts — pass --account <id> to disambiguate."));

        var target = matches[0];

        // Resolve the tenant to the target account BEFORE the reset so
        // IAuditWriter (which fails closed on an unresolved tenant) can stamp the
        // break-glass audit row with the correct AccountId.
        tenant.Resolve(target.AccountId);

        var password = TemporaryPassword.Generate();
        var reset = await identity.BreakGlassResetAsync(
            target.AccountId, target.Id, password, reason, ct);
        if (reset.IsFailure)
            return Result.Failure<AdminRecoveryResult>(reset.Error);

        return Result.Success(new AdminRecoveryResult(target.Email!, target.AccountId, password));
    }
}

public sealed record AdminRecoveryResult(string Email, Guid AccountId, string TemporaryPassword);
