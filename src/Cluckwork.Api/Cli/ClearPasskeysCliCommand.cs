using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Farm.Contracts;

namespace Cluckwork.Api.Cli;

// #1284 (record 1282 decision 10): the operator removes every passkey of one
// farm, or of every farm, before turning passkeys off or moving the site to a
// new address. Each farm runs in its own scope and transaction. No scope, or
// both, refuses and changes nothing; an unknown farm code exits non-zero.
public sealed class ClearPasskeysCliCommand : ICliCommand
{
    public string Name => "clear-passkeys";

    public async Task<int> RunAsync(WebApplication app, string[] args)
    {
        try
        {
            var all = args.Contains("--all");
            var slug = AccountSlugLookup.Normalize(CliDispatcher.ArgValue(args, "--farm"));
            if (all == slug is not null)
            {
                await Console.Error.WriteLineAsync(
                    "clear-passkeys requires exactly one of --all or --farm <farm-code>. Nothing was changed.");
                return 1;
            }

            List<(string Slug, Guid Id)> farms = [];
            using (var lookup = app.Services.CreateScope())
            {
                var directory = lookup.ServiceProvider.GetRequiredService<IFarmDirectory>();
                foreach (var code in all ? (await directory.ListAsync()).Select(f => f.Slug) : [slug!])
                {
                    if (await directory.FindIdBySlugAsync(code) is not { } id)
                    {
                        await Console.Error.WriteLineAsync($"No farm with code '{code}'. Nothing was changed.");
                        return 1;
                    }
                    farms.Add((code, id));
                }
            }

            var reason = CliDispatcher.ArgValue(args, "--reason");
            foreach (var (code, id) in farms)
            {
                using var scope = app.Services.CreateScope();
                var cleared = await scope.ServiceProvider.GetRequiredService<IAccessOperations>()
                    .ClearPasskeysAsync(id, reason, CancellationToken.None);
                await Console.Out.WriteLineAsync(
                    $"Farm '{code}': removed the passkeys of {cleared} user(s); their sessions and connected apps ended.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"clear-passkeys failed: {ex.Message}");
            return 1;
        }
    }
}
