using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// Step is set when a step above the watermark matched; Reused when only steps
// at or below it did.
internal readonly record struct TotpMatch(long? Step, bool Reused);

// #1274 decision 14: Identity's AuthenticatorTokenProvider returns only a bool,
// and the watermark needs the matched step. Same algorithm: RFC 6238 with
// HMAC-SHA1, 6 digits, 30-second steps, -2..+2. Only TwoFactorCredentials calls
// it (G3.20).
internal static class TotpMatcher
{
    private const int StepSeconds = 30;
    private const int Window = 2;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    internal static long StepAt(DateTimeOffset now) => now.ToUnixTimeSeconds() / StepSeconds;

    internal static int Compute(ReadOnlySpan<byte> key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);
        var offset = hash[^1] & 0x0F;
        return (BinaryPrimitives.ReadInt32BigEndian(hash[offset..]) & 0x7FFFFFFF) % 1_000_000;
    }

    // Compares all five candidates, with no early exit.
    internal static TotpMatch MatchStep(string base32Key, int code, long watermark, DateTimeOffset now)
    {
        var key = DecodeBase32(base32Key);
        var current = StepAt(now);
        long? accepted = null;
        var reused = false;
        for (var step = current - Window; step <= current + Window; step++)
        {
            if ((Compute(key, step) ^ code) != 0) continue;
            if (step > watermark) accepted ??= step;
            else reused = true;
        }
        return new TotpMatch(accepted, accepted is null && reused);
    }

    // The 32-character key UserManager.GenerateNewAuthenticatorKey returns, A-Z2-7, unpadded.
    internal static byte[] DecodeBase32(string value)
    {
        var bytes = new byte[value.Length * 5 / 8];
        int buffer = 0, bits = 0, index = 0;
        foreach (var c in value)
        {
            var digit = Base32Alphabet.IndexOf(c);
            if (digit < 0) throw new FormatException("An authenticator key is base32.");
            buffer = ((buffer << 5) | digit) & 0xFFF;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes[index++] = (byte)(buffer >> bits);
        }
        return bytes;
    }
}
