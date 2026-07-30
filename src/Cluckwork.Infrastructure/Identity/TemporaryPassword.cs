namespace Cluckwork.Infrastructure.Identity;

using System.Security.Cryptography;

// #265 — generates a strong random password for break-glass account recovery.
// It satisfies the Identity policy configured in Program.cs (RequiredLength = 12
// plus the framework defaults: at least one lowercase, uppercase, digit and
// non-alphanumeric character). Length 20 for margin. The operator is shown it
// once by the `recover-admin` command and is expected to rotate it immediately
// after logging in.
//
// .NET has no built-in policy-compliant password generator — the classic
// System.Web.Security.Membership.GeneratePassword is full-framework only, and
// ASP.NET Core Identity provides the password policy/hasher but no generator, so
// a NuGet dependency for this tiny amount of logic isn't worth the supply-chain
// surface. The randomness itself IS the BCL's CSPRNG: RandomNumberGenerator's
// GetItems (unbiased selection) + Shuffle (unbiased Fisher–Yates). The only
// custom part is the character sets — deliberately omitting visually ambiguous
// glyphs (0/O, 1/l/I) so the operator can transcribe the one-time value — and
// the guarantee of one character from each required policy class.
internal static class TemporaryPassword
{
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";  // no 'l'
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";  // no 'I', 'O'
    private const string Digits = "23456789";                // no '0', '1'
    private const string Symbols = "!@#$%^&*-_=+?";
    private const string All = Lower + Upper + Digits + Symbols;
    private const int Length = 20;

    public static string Generate()
    {
        Span<char> buffer = stackalloc char[Length];

        // One guaranteed character from each required policy class...
        RandomNumberGenerator.GetItems(Lower, buffer[..1]);
        RandomNumberGenerator.GetItems(Upper, buffer[1..2]);
        RandomNumberGenerator.GetItems(Digits, buffer[2..3]);
        RandomNumberGenerator.GetItems(Symbols, buffer[3..4]);
        // ...the rest from the full alphabet...
        RandomNumberGenerator.GetItems(All, buffer[4..]);
        // ...then shuffle so the guaranteed characters aren't in fixed positions.
        RandomNumberGenerator.Shuffle(buffer);

        return new string(buffer);
    }
}
