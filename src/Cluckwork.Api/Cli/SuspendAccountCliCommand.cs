using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.Cli;

// `suspend-account --slug <s> [--reason <text>]` (#534) — takes a farm offline.
// Enforcement is already live (#532): Account.IsActive is read by
// CredentialEpochMiddleware on EVERY authenticated request, so suspension bites
// on the next request rather than at token expiry, and login/refresh refuse
// separately. This verb is the operator surface for it.
//
// Same run-then-exit shape as the rest of the family, classified OneShot
// automatically via CliDispatcher.Commands (#347), and deliberately NOT
// environment-gated — it must work against a real Production database. Safety
// comes from needing shell access to the deployment, plus a conspicuous
// Account.Suspend audit row carrying --reason.
//
// Re-running against an already-suspended farm exits 0 and appends no second
// audit row, but DOES re-run the revoke sweep — the only operator-reachable way
// to mop up a credential minted inside the login/suspend race window that
// AccountSuspensionService documents.
public sealed class SuspendAccountCliCommand : ICliCommand
{
    public string Name => "suspend-account";

    public async Task<int> RunAsync(WebApplication app, string[] args)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var slug = AccountSlugLookup.Normalize(CliDispatcher.ArgValue(args, "--slug"));
            if (slug is null)
            {
                await Console.Error.WriteLineAsync("suspend-account requires --slug <farm-code>.");
                return 1;
            }

            var accountId = await scope.ServiceProvider.GetRequiredService<IFarmDirectory>().FindIdBySlugAsync(slug);
            if (accountId is null)
            {
                await Console.Error.WriteLineAsync($"No farm with code '{slug}'.");
                return 1;
            }

            var result = await scope.ServiceProvider.GetRequiredService<IAccessAccountLifecycle>().SuspendAccountAsync(
                accountId.Value, CliDispatcher.ArgValue(args, "--reason"), CancellationToken.None);
            if (result.IsFailure)
            {
                await Console.Error.WriteLineAsync(
                    $"suspend-account failed: {result.Error.Code} — {result.Error.Description}");
                return 1;
            }

            // Only the slug is echoed, never the farm NAME: the name is
            // tenant-controlled free text whose validator bounds only length, so
            // printing it needs the control-character strip ListAccountsCliCommand
            // carries (#560). The slug's regex makes it safe by construction.
            await Console.Out.WriteLineAsync(result.Value.Changed
                ? $"Farm '{slug}' is suspended. Every session was revoked; sign-in and every "
                  + "authenticated request are refused from now on."
                : $"Farm '{slug}' was already suspended — no audit row written. Its sessions were "
                  + "revoked again.");
            return 0;
        }
        catch (Exception ex)
        {
            // Fail-loud per the family's contract: an unexpected error (DB
            // unreachable, a lost concurrency race) becomes exit 1 and a clean
            // stderr line, never a raw stack trace. The service's transaction
            // rolls back, so nothing is left half-changed.
            await Console.Error.WriteLineAsync($"suspend-account failed: {ex.Message}");
            return 1;
        }
    }
}

// Shared by seed, suspend-account, reactivate-account and rename-account (#534).
// Each verb resolves the normalized code through Farm's IFarmDirectory (#858).
internal static class AccountSlugLookup
{
    // Slugs are stored already-lowercased (Account.ValidateSlug REJECTS uppercase
    // rather than folding it), so a plain equality match is exact — but an
    // operator typing SECOND-FARM at a shell should not get "no such farm", so
    // fold the INPUT. Invariant, not current-culture: a Turkish locale's dotless
    // ı would otherwise turn a valid code into one that matches nothing.
    internal static string? Normalize(string? slug) =>
        string.IsNullOrWhiteSpace(slug) ? null : slug.Trim().ToLowerInvariant();
}
