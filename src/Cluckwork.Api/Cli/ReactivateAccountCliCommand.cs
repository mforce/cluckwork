namespace Cluckwork.Api.Cli;

using Cluckwork.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

// `reactivate-account --slug <s> [--reason <text>]` (#534) — brings a suspended
// farm back. Suspension deletes nothing, so reactivation restores the farm
// exactly — with one deliberate exception: sessions that predate the suspension
// stay dead, because both directions revoke (AccountSuspensionService).
//
// Re-running against an already-active farm exits 0, changes nothing, writes no
// audit row and — importantly — does NOT revoke: that would sign out every
// member of staff mid-shift for a command that did nothing.
public sealed class ReactivateAccountCliCommand : ICliCommand
{
    public string Name => "reactivate-account";

    public async Task<int> RunAsync(WebApplication app, string[] args)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var slug = AccountSlugLookup.Normalize(CliDispatcher.ArgValue(args, "--slug"));
            if (slug is null)
            {
                await Console.Error.WriteLineAsync("reactivate-account requires --slug <farm-code>.");
                return 1;
            }

            var accountId = await AccountSlugLookup.ResolveAsync(scope.ServiceProvider, slug);
            if (accountId is null)
            {
                await Console.Error.WriteLineAsync($"No farm with code '{slug}'.");
                return 1;
            }

            var service = scope.ServiceProvider.GetRequiredService<AccountSuspensionService>();
            var result = await service.ReactivateAsync(
                accountId.Value, CliDispatcher.ArgValue(args, "--reason"), CancellationToken.None);
            if (result.IsFailure)
            {
                await Console.Error.WriteLineAsync(
                    $"reactivate-account failed: {result.Error.Code} — {result.Error.Description}");
                return 1;
            }

            await Console.Out.WriteLineAsync(result.Value.Changed
                ? $"Farm '{slug}' is active again. Its data is intact; sessions issued before the "
                  + "suspension stay revoked and every user must sign in again."
                : $"Farm '{slug}' was already active — nothing changed, and no session was revoked.");
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"reactivate-account failed: {ex.Message}");
            return 1;
        }
    }
}
