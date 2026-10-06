using System.Text.RegularExpressions;

namespace Cluckwork.Domain.Modules.Farm.Contracts;

// Farm code (#531). Lowercase, URL-safe, stored ALREADY-NORMALIZED so a
// plain unique index suffices — deliberately NOT a lower("Slug") expression
// index (the four in InitialCreate are un-regenerable #407 fixtures; no
// reason to mint a fifth). Renameable since #732 by the `rename-account`
// verb and nothing else — there is deliberately no endpoint or Settings
// field, and no retired-code list: a code a farm has moved off is
// immediately reusable, which docs/decisions/732-farm-code-rename.md
// records as the accepted cost. The one rule for provisioning, rename and
// sign-in (#1116); Account's slug members delegate here.
public static class FarmCode
{
    public const int MaxLength = 32;

    public static readonly IReadOnlySet<string> Reserved =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "api", "admin", "www", "health", "app", "static", "assets", "login", "auth",
        };

    // 3–32 chars, lowercase alnum + hyphen, no leading/trailing hyphen.
    // UPPERCASE IS REJECTED, not folded: the stored value is guaranteed
    // lowercase, which is exactly what lets the unique index be plain.
    private static readonly Regex Pattern =
        new("^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$", RegexOptions.Compiled);

    public static Result<string> TryValidate(string? slug)
    {
        var normalized = (slug ?? string.Empty).Trim();
        if (!Pattern.IsMatch(normalized))
            return Result.Failure<string>(Error.Validation(
                "Account.SlugInvalid",
                $"'{slug}' is not a valid farm code (lowercase letters, digits and hyphens, " +
                "3–32 characters, no leading or trailing hyphen)."));
        if (Reserved.Contains(normalized))
            return Result.Failure<string>(Error.Validation(
                "Account.SlugInvalid", $"'{normalized}' is a reserved farm code."));
        return Result.Success(normalized);
    }
}
