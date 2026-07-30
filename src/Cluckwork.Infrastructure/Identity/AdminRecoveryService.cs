namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
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
    IIdentityProvider identity)
{
    public async Task<Result<AdminRecoveryResult>> RecoverAsync(
        string? email, Guid? accountId, string? reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return Result.Failure<AdminRecoveryResult>(Error.Validation(
                "Recovery.EmailRequired", "An --email is required."));
        var normalized = email.Trim();

        // db.Users (ASP.NET Identity) carries no tenant query filter, so this
        // lookup works before any tenant is resolved. Identity's unique-username
        // index makes an email globally unique today; the optional --account
        // filter and the ambiguity guard below are defensive for the dormant
        // multi-tenant future, where the same email could exist per account.
        var matches = await db.Users
            .Where(u => u.Email == normalized && (accountId == null || u.AccountId == accountId))
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
