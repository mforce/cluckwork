using System.Diagnostics.CodeAnalysis;

namespace Cluckwork.Application.Modules.Access.Contracts;

// #1274 "Types": parsed once, after the challenge is read; after that a code is
// never a string. ToString is sealed so no derived record prints its member.
public abstract record SecondFactorCode
{
    // Identity's recovery-code alphabet (UserManager.cs:2513-2515): no vowels,
    // no 0, 1 or look-alike letters.
    public const string RecoveryAlphabet = "23456789BCDFGHJKMNPQRTVWXY";
    public const int RecoveryLength = 16;

    private SecondFactorCode() { }

    // Exactly 6 ASCII digits after removing spaces.
    public sealed record Totp(int Value) : SecondFactorCode;

    // XXXX-XXXX-XXXX-XXXX in upper case (decision 9).
    public sealed record Recovery(string Normalized) : SecondFactorCode;

    public sealed override string ToString() => "[redacted]";

    public static bool TryParse(string? raw, [NotNullWhen(true)] out SecondFactorCode? code)
    {
        code = null;
        if (raw is null) return false;

        var digits = raw.Replace(" ", "");
        if (digits.Length == 6 && digits.All(char.IsAsciiDigit))
        {
            code = new Totp(int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        if (NormalizeRecovery(digits) is not { } normalized) return false;
        code = new Recovery(normalized);
        return true;
    }

    // The one normal form (decision 9): spaces and hyphens removed, upper case,
    // 16 characters of the alphabet, then XXXX-XXXX-XXXX-XXXX. Null otherwise.
    public static string? NormalizeRecovery(string raw)
    {
        var compact = raw.Replace(" ", "").Replace("-", "").ToUpperInvariant();
        return compact.Length == RecoveryLength && compact.All(RecoveryAlphabet.Contains)
            ? string.Join('-', compact.Chunk(4).Select(c => new string(c)))
            : null;
    }
}
