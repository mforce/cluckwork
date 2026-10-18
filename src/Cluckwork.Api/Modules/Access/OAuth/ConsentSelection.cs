using System.Collections.Immutable;
using System.Text.Json;
using Cluckwork.Domain.Common;
using Microsoft.Extensions.Primitives;

namespace Cluckwork.Api.Modules.Access.OAuth;

// #1326 — the scopes the person ticked on the consent screen, G. Only a subset of what the
// app asked for (R) that the person's role can use (U) gets here; an empty one is Cancel.
internal sealed class ConsentSelection
{
    public const string InvalidCode = "OAuth.InvalidConsentScopes";

    private ConsentSelection(ImmutableArray<string> scopes) => Scopes = scopes;

    public ImmutableArray<string> Scopes { get; }

    // The SPA sends a JSON array of scope names. A request without the header comes from an
    // SPA older than #1326 and gets every requested scope the role can use. JSON tells an
    // empty choice, [], apart from no choice at all.
    public static Result<ConsentSelection> Read(
        IReadOnlyCollection<string> requested, IReadOnlySet<string> usable, StringValues header)
    {
        if (header.Count == 0)
            return new ConsentSelection([.. requested.Where(usable.Contains).Order(StringComparer.Ordinal)]);
        if (header.Count != 1)
            return Invalid("Send the chosen scopes once.");

        string?[]? names;
        try
        {
            names = JsonSerializer.Deserialize<string?[]>(header[0]!);
        }
        catch (JsonException)
        {
            return Invalid("The chosen scopes are not a JSON array of scope names.");
        }
        if (names is null || names.Any(string.IsNullOrWhiteSpace))
            return Invalid("The chosen scopes are not a JSON array of scope names.");

        var selected = new HashSet<string>(names!, StringComparer.Ordinal);
        if (!selected.IsSubsetOf(requested))
            return Invalid("A chosen scope is not one the app asked for.");
        if (!selected.IsSubsetOf(usable))
            return Invalid("A chosen scope is not one your role can use.");
        return new ConsentSelection([.. selected.Order(StringComparer.Ordinal)]);
    }

    private static Result<ConsentSelection> Invalid(string description) =>
        Result.Failure<ConsentSelection>(Error.Validation(InvalidCode, description));
}
