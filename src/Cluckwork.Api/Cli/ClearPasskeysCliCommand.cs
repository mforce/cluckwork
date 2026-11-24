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
            // One pass reads every argument. A flag the verb does not know
            // (--farm=x, --Farm), or --farm or --reason with no value after it,
            // refuses, so a mistyped argument never widens the run.
            var all = false;
            string? farm = null, reason = null;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--all")
                {
                    all = true;
                    continue;
                }
                if (args[i] is not ("--farm" or "--reason"))
                    return await RefuseAsync($"clear-passkeys does not know '{args[i]}'.");
                if (i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    return await RefuseAsync($"clear-passkeys needs a value after {args[i]}.");
                if (args[i] == "--farm") farm = args[++i];
                else reason = args[++i];
            }

            // Exactly one scope: --all with no --farm, or --farm with a farm
            // code (decision 10).
            var slug = AccountSlugLookup.Normalize(farm);
            if (all ? farm is not null : slug is null)
                return await RefuseAsync("clear-passkeys requires exactly one of --all or --farm <farm-code>.");

            List<(string Slug, Guid Id)> farms = [];
            using (var lookup = app.Services.CreateScope())
            {
                var directory = lookup.ServiceProvider.GetRequiredService<IFarmDirectory>();
                foreach (var code in all ? (await directory.ListAsync()).Select(f => f.Slug) : [slug!])
                {
                    if (await directory.FindIdBySlugAsync(code) is not { } id)
                        return await RefuseAsync($"No farm with code '{code}'.");
                    farms.Add((code, id));
                }
            }

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

    private static async Task<int> RefuseAsync(string message)
    {
        await Console.Error.WriteLineAsync(
            message + " Use --all or --farm <farm-code>, and --reason <text>. Nothing was changed.");
        return 1;
    }
}
