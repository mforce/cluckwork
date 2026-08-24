namespace Cluckwork.Api.Cli;

using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// `bootstrap-admin --email <e>` (#283) — first-run admin provisioning. Same
// run-then-exit, fail-loud shape as seed/migrate/recover-admin: migrates the
// schema (idempotent, like every other one-shot verb), then creates the
// default account's first Owner with a FRESHLY GENERATED password and
// MustChangePassword=true — but only if no Owner exists yet. NEVER bakes a
// credential: the #283 migration seeds roles/grades/the default account
// (static reference data), never a user row.
//
// The generated password is the only copy that will ever exist: printed to
// stdout ONCE, NEVER the logger/OTLP pipeline — identical rule to
// recover-admin (#265), so it can never land in structured logs. A re-run
// against an already-provisioned account is a safe, silent (no secret
// reprinted) success — see FirstRunAdminService for the idempotency logic.
//
// Deliberately a separate credential/flow from #308's browser step-up grants:
// this is an offline, pre-auth, one-shot secret with its own audience
// (whoever has shell access to run the command) and its own lifetime (dies
// the moment ChangeOwnPasswordAsync consumes it) — it must never be conflated
// with a signed-in Owner's step-up re-confirmation.
public sealed class BootstrapAdminCliCommand : ICliCommand
{
    public string Name => "bootstrap-admin";

    public async Task<int> RunAsync(WebApplication app, string[] args)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;

            await sp.GetRequiredService<AppDbContext>().Database.MigrateAsync();

            var email = CliDispatcher.ArgValue(args, "--email");
            var provisioning = sp.GetRequiredService<FirstRunAdminService>();
            var result = await provisioning.ProvisionAsync(email, CancellationToken.None);
            if (result.IsFailure)
            {
                await Console.Error.WriteLineAsync(
                    $"Bootstrap failed: {result.Error.Code} — {result.Error.Description}");
                return 1;
            }

            var outcome = result.Value;
            if (outcome.WasAlreadyProvisioned)
            {
                // No secret to print — the account already has an Owner
                // (idempotent re-run). Deliberately no detail about who, so a
                // repeat invocation can't leak anything beyond "already done".
                await Console.Out.WriteLineAsync(
                    "Admin already provisioned (an Owner exists in the default account); nothing to do.");
                return 0;
            }

            // Print to stdout (NOT the logger) so the one-time password is
            // shown to the operator and never lands in structured logs / the
            // OTLP pipeline. NOTE: a host's stdout collector (docker logs,
            // journald, a platform log pipeline) may still capture it — treat
            // the value as sensitive, same as the recover-admin runbook
            // instructs (#265 review).
            // #589 — the FARM CODE is named explicitly because it is the one of the
            // three login inputs the operator has no other source for: the email and
            // password are the operator's own, while the farm code is furnished by a
            // command. Printing it here removes a recovery step rather than being the
            // only route — the read-only `list-accounts` verb also prints it, and the
            // SPA can supply it from a remembered code or a /login?farm= link once
            // either exists (on a first run, neither does). The account GUID is
            // diagnostic only. Without this line the command still ends with "log in
            // with this now" while withholding one of the three things that takes:
            // #532 changed the login contract (farm code became a required input) and
            // this caller was missed.
            await Console.Out.WriteLineAsync(
                $"First-run admin provisioned: {outcome.Email} on farm {outcome.Slug} "
                + $"(account {outcome.AccountId}).");
            await Console.Out.WriteLineAsync($"Temporary password: {outcome.TemporaryPassword}");
            await Console.Out.WriteLineAsync(
                "Log in with this now — the app requires a new password before anything else works.");
            return 0;
        }
        catch (Exception ex)
        {
            // Fail-loud, same as every other one-shot verb: a raw stack trace
            // never reaches the operator's console.
            await Console.Error.WriteLineAsync($"Bootstrap failed: {ex.Message}");
            return 1;
        }
    }
}
