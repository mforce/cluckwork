namespace Cluckwork.Api.Cli;

using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

// Creates one farm, its canonical reference data, and its first Owner in one
// transaction. It deliberately does not migrate: production runs this through
// the least-privilege runtime database role after the migrate job has finished.
public sealed class ProvisionAccountCliCommand : ICliCommand
{
    public string Name => "provision-account";

    public async Task<int> RunAsync(WebApplication app, string[] args)
    {
        try
        {
            var slug = Account.TryValidateSlug(CliDispatcher.ArgValue(args, "--slug"));
            if (slug.IsFailure)
            {
                await Console.Error.WriteLineAsync(
                    $"Provisioning failed: {slug.Error.Code} — {slug.Error.Description}");
                return 1;
            }

            // The farm code is immutable during this epic. Echo only its
            // normalized value before any write; the name is tenant-controlled
            // terminal text and is deliberately never echoed here (#560).
            await Console.Out.WriteLineAsync($"Farm code: {slug.Value}");

            using var scope = app.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
                .ProvisionAsync(
                    CliDispatcher.ArgValue(args, "--name"),
                    slug.Value,
                    CliDispatcher.ArgValue(args, "--owner-email"),
                    CliDispatcher.ArgValue(args, "--time-zone"),
                    CliDispatcher.ArgValue(args, "--locale"),
                    CliDispatcher.ArgValue(args, "--currency"));

            if (result.IsFailure)
            {
                await Console.Error.WriteLineAsync(
                    $"Provisioning failed: {result.Error.Code} — {result.Error.Description}");
                return 1;
            }

            var outcome = result.Value;
            await Console.Out.WriteLineAsync(
                $"Farm provisioned: {outcome.Slug} (account {outcome.AccountId}); Owner {outcome.OwnerEmail}.");
            await Console.Out.WriteLineAsync($"Temporary password: {outcome.TemporaryPassword}");
            await Console.Out.WriteLineAsync(
                "Log in with this now — the app requires a new password before anything else works.");
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Provisioning failed: {ex.Message}");
            return 1;
        }
    }
}
