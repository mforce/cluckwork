using System.Diagnostics.CodeAnalysis;
using Cluckwork.Application.Common;

namespace Cluckwork.Application.Modules.Access.Contracts;

// #1274 "Types": parsed once, after the challenge is read; after that a code is
// never a string. ToString is sealed so no derived record prints its member.
// [Credential]: the code sits in Value and Normalized, names that G3.29's walk
// does not read as a credential.
[Credential]
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

        if (NormalizeRecovery(raw) is not { } normalized) return false;
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

// #1285 (record 1282 decision 7): what the pending 401's secondFactors lists.
public static class SecondFactorKinds
{
    public const string Authenticator = "authenticator";
    public const string Passkey = "passkey";
}

// #1285: what the login code step accepts, a code string, parsed after the
// challenge is read, or a passkey assertion made at RequestHost.
[Credential]
public abstract record SecondFactorInput
{
    private SecondFactorInput() { }

    public sealed record Code(string? Raw) : SecondFactorInput;

    // CredentialJson is the browser's PublicKeyCredential JSON, passed to
    // Identity unparsed.
    public sealed record Passkey(Guid Ceremony, string CredentialJson, string RequestHost) : SecondFactorInput;

    public sealed override string ToString() => "[redacted]";
}
