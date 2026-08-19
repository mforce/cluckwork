namespace Cluckwork.Api.Cli;

using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// `list-accounts` (#531) — a read-only operator verb that prints every farm's
// code, name and active state. Moved forward from #533 deliberately: #532 makes
// the farm code mandatory at login, so the first upgraded deployment needs a
// supported way to discover its own code BEFORE provisioning (#533) exists.
//
// Reads ACROSS accounts, so it runs with an unresolved tenant and must use
// IgnoreQueryFilters() on purpose (that call site is on #536's justified
// allow-list). Run-then-exit like the other verbs; classified OneShot
// automatically via CliDispatcher.Commands (#347) and NOT environment-gated.
// It does not migrate — like recover-admin it only reads an already-migrated
// database — and it wraps its work fail-loud so a missing/unreachable database
// is a clean exit 1, never an unhandled crash.
public sealed class ListAccountsCliCommand : ICliCommand
{
    public string Name => "list-accounts";

    public async Task<int> RunAsync(WebApplication app, string[] args)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var accounts = await db.Accounts
                .IgnoreQueryFilters()
                .OrderBy(a => a.Slug)
                .Select(a => new { a.Slug, a.Name, a.IsActive })
                .ToListAsync();

            foreach (var account in accounts)
                await Console.Out.WriteLineAsync(
                    $"{account.Slug}\t{SanitizeForDisplay(account.Name)}\t{(account.IsActive ? "active" : "suspended")}");

            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"list-accounts failed: {ex.Message}");
            return 1;
        }
    }

    // #560 (codex round 2) — the account NAME is tenant-controlled free text and
    // its write validator (UpdateFarmSettingsValidator) bounds only length, so it
    // can carry CR/LF/tab/ANSI-escape characters. Printed verbatim into a terminal
    // row those let one tenant forge or obscure another farm's line. Replace every
    // control character with a space — the same char.IsControl strip
    // ClientErrorEndpoints applies to its rendered fields against log forging.
    // Slug is control-character-free by its regex and IsActive is a bool, so only
    // Name needs this.
    internal static string SanitizeForDisplay(string value) =>
        !value.Any(char.IsControl)
            ? value
            : string.Create(value.Length, value, static (chars, source) =>
            {
                for (var i = 0; i < source.Length; i++)
                    chars[i] = char.IsControl(source[i]) ? ' ' : source[i];
            });
}
