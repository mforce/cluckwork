namespace Cluckwork.Api.Cli;

using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// `recover-admin --email <e> [--account <guid>] [--reason <t>]` (#265) — offline
// break-glass recovery for a locked-out account (a sole Owner with a lost
// password and no email/SMTP reset path would otherwise need direct DB surgery).
// Same run-then-exit shape as `seed`, but deliberately NOT environment-gated: it
// must work against a real Production database. Its safety comes from requiring
// shell access to the deployment, plus a conspicuous "User.BreakGlassReset" audit
// row.
public sealed class RecoverAdminCliCommand : ICliCommand
{
    public string Name => "recover-admin";

    public async Task<int> RunAsync(WebApplication app, string[] args)
    {
        try
        {
            using var recoveryScope = app.Services.CreateScope();
            var sp = recoveryScope.ServiceProvider;

            var accountArg = CliDispatcher.ArgValue(args, "--account");
            Guid? accountId = null;
            if (accountArg is not null)
            {
                if (!Guid.TryParse(accountArg, out var parsedAccount))
                {
                    await Console.Error.WriteLineAsync($"Invalid --account '{accountArg}' — must be a GUID.");
                    return 1;
                }
                accountId = parsedAccount;
            }

            await sp.GetRequiredService<AppDbContext>().Database.MigrateAsync();

            var recovery = sp.GetRequiredService<AdminRecoveryService>();
            var recovered = await recovery.RecoverAsync(
                CliDispatcher.ArgValue(args, "--email"), accountId,
                CliDispatcher.ArgValue(args, "--reason"), CancellationToken.None);
            if (recovered.IsFailure)
            {
                await Console.Error.WriteLineAsync(
                    $"Recovery failed: {recovered.Error.Code} — {recovered.Error.Description}");
                return 1;
            }

            // Print to stdout (NOT the logger) so the one-time password is shown to
            // the operator and never lands in structured logs / the OTLP pipeline.
            // NOTE: a host's stdout collector (docker logs, journald, a platform log
            // pipeline) may still capture it — treat the value as sensitive and
            // rotate it on first login, as the runbook instructs (#265 review).
            var outcome = recovered.Value;
            await Console.Out.WriteLineAsync(
                $"Break-glass reset complete for {outcome.Email} (account {outcome.AccountId}). " +
                "All existing sessions were revoked.");
            await Console.Out.WriteLineAsync($"Temporary password: {outcome.TemporaryPassword}");
            await Console.Out.WriteLineAsync(
                "Log in with this now and change it immediately (Account → change password).");
            return 0;
        }
        catch (Exception ex)
        {
            // Fail-loud per the runbook: an unexpected error (DB unreachable, a
            // concurrent reset losing the CAS race, ...) becomes exit 1 + a clean
            // stderr line, not a raw stack trace. The reset path's transaction rolls
            // back, so nothing is left half-changed (#265 review).
            await Console.Error.WriteLineAsync($"Recovery failed: {ex.Message}");
            return 1;
        }
    }
}
