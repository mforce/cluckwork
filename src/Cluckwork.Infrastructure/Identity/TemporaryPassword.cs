namespace Cluckwork.Infrastructure.Identity;

using System.Security.Cryptography;

// #265 — generates a strong random password for break-glass account recovery.
// It satisfies the Identity policy configured in Program.cs (RequiredLength = 12
// plus the framework defaults: at least one lowercase, uppercase, digit and
// non-alphanumeric character). Length 20 for margin. The operator is shown it
// once by the `recover-admin` command and is expected to rotate it immediately
// after logging in.
//
// Character sets deliberately omit visually ambiguous glyphs (0/O, 1/l/I) so the
// operator can transcribe the one-time value without confusion.
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
        // Guarantee one character from each required policy class, fill the rest
        // from the full alphabet, then cryptographically shuffle so the
        // guaranteed characters do not sit in fixed, predictable positions.
        var chars = new List<char> { Pick(Lower), Pick(Upper), Pick(Digits), Pick(Symbols) };
        while (chars.Count < Length)
            chars.Add(Pick(All));
        Shuffle(chars);
        return new string([.. chars]);
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];

    private static void Shuffle(IList<char> list)
    {
        // Fisher–Yates with a CSPRNG-backed unbiased index.
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
