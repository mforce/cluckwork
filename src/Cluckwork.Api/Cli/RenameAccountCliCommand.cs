namespace Cluckwork.Api.Cli;

using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

// `rename-account --slug <current> --new-slug <new> [--reason <text>]` (#732) — changes a
// farm's code. The reason it exists: a database upgraded from before multi-farm tenancy
// gets `default-farm` from the AddAccountSlug migration, nothing asks at migration time,
// and #731's only path was a hand-guarded UPDATE that bumped Version by hand, wrote no
// audit row and checked neither the pattern nor the reserved set. This is that write with
// the domain in front of it.
//
// Same run-then-exit shape as the lifecycle verbs, classified OneShot automatically via
// CliDispatcher.Commands (#347), and deliberately NOT environment-gated — it has to work
// against a real Production database. Safety is shell access plus a conspicuous
// Account.Rename audit row carrying from/to and --reason.
//
// WHAT THE OPERATOR MUST KNOW, and the two things this prints rather than assumes:
//   * Run list-accounts first. A code a farm has moved off is immediately reusable, so
//     --slug names whoever holds that code NOW, not the farm you meant last week.
//   * Existing sessions keep working: cookies and tokens bind to the account id. What
//     goes stale is client-side and cosmetic, and it does not clear itself. The next
//     explicit sign-in with the NEW code prepends it to that device's remembered-code
//     roster and refreshes the palette cache under the new key; the OLD remembered code
//     stays on the roster until the user picks Forget, and offering it returns
//     Auth.UnknownFarmCode unless another farm has since reused it.
//
// ALL CURRENT stderr paths in this verb route through one sink, WriteErrorAsync, which
// is where the sanitization lives. Round 1 sanitized the rejected --new-slug line and
// left the unknown-code line raw, which is the shape a per-line fix produces. A comment
// cannot make a second raw write impossible, so it does not claim to: the claim above is
// checked by RenameAccountDocsTests
// .RenameVerb_HasExactlyOneStderrSink_AndItIsWriteErrorAsync, which reads this file and
// reds on any direct stderr write outside the sink — including one written in a comment,
// since it counts occurrences in the source text rather than in the syntax tree.
public sealed class RenameAccountCliCommand : ICliCommand
{
    public string Name => "rename-account";

    // #732 review round 2 — both stderr messages that quote raw argv (the rejected
    // --new-slug, whose description quotes the value, and the unknown --slug) would
    // otherwise break this verb's one-line contract and let a second line forge
    // whatever an operator's terminal reads next. Same char.IsControl strip
    // list-accounts applies to the tenant-controlled farm name (#560), applied to the
    // whole assembled message rather than to one interpolation: the messages that are
    // safe by construction pay nothing, and the ones that are not cannot be missed.
    private static Task WriteErrorAsync(string message) =>
        Console.Error.WriteLineAsync(ListAccountsCliCommand.SanitizeForDisplay(message));

    public async Task<int> RunAsync(WebApplication app, string[] args)
    {
        try
        {
            using var scope = app.Services.CreateScope();

            // The CURRENT code is folded like every other verb's --slug: an operator
            // typing SECOND-FARM at a shell means second-farm. The NEW code is NOT
            // folded, and that asymmetry is the domain's rule, not a slip —
            // TryValidateSlug rejects uppercase so the stored value is guaranteed
            // lowercase, which is what lets IX_Accounts_Slug be a plain index.
            var current = AccountSlugLookup.Normalize(CliDispatcher.ArgValue(args, "--slug"));
            if (current is null)
            {
                await WriteErrorAsync(
                    "rename-account requires --slug <current-farm-code>.");
                return 1;
            }

            // Checked for PRESENCE before the domain runs: TryValidateSlug's description
            // quotes the offending value, and for an absent flag that is an empty string —
            // an operator would get "'' is not a valid farm code" instead of the flag name.
            var requested = CliDispatcher.ArgValue(args, "--new-slug");
            if (requested is null)
            {
                await WriteErrorAsync(
                    "rename-account requires --new-slug <new-farm-code>.");
                return 1;
            }

            var newSlug = Account.TryValidateSlug(requested);
            if (newSlug.IsFailure)
            {
                // The description QUOTES the rejected value, which is raw argv. It is
                // NOT stripped here — WriteErrorAsync strips the assembled message, so
                // this line reads exactly like the other four.
                await WriteErrorAsync(
                    $"rename-account failed: {newSlug.Error.Code} — {newSlug.Error.Description}");
                return 1;
            }

            var accountId = await AccountSlugLookup.ResolveAsync(scope.ServiceProvider, current);
            if (accountId is null)
            {
                await WriteErrorAsync($"No farm with code '{current}'.");
                return 1;
            }

            var service = scope.ServiceProvider.GetRequiredService<AccountRenameService>();
            var result = await service.RenameAsync(
                current, newSlug.Value, CliDispatcher.ArgValue(args, "--reason"),
                CancellationToken.None);
            if (result.IsFailure)
            {
                await WriteErrorAsync(
                    $"rename-account failed: {result.Error.Code} — {result.Error.Description}");
                return 1;
            }

            // Both codes are echoed and nothing else: they are slug-regex values, safe by
            // construction. The farm NAME is tenant-controlled free text whose validator
            // bounds only length, so printing it needs ListAccountsCliCommand's
            // control-character strip (#560) — which is why no verb in this family prints it.
            await Console.Out.WriteLineAsync(result.Value.Changed
                ? $"Farm renamed: {current} → {newSlug.Value}. Existing sessions keep working; "
                  + "users must use the new code at their next sign-in."
                : $"Farm '{current}' already has that code — nothing changed and no audit row "
                  + "was written.");
            return 0;
        }
        catch (Exception ex)
        {
            // Fail-loud per the family's contract: an unexpected error (DB unreachable, a
            // lost concurrency race) is exit 1 and one clean stderr line, never a stack
            // trace. The service's transaction rolls back, so nothing is half-changed.
            await WriteErrorAsync($"rename-account failed: {ex.Message}");
            return 1;
        }
    }
}
