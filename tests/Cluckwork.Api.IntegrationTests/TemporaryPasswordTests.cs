namespace Cluckwork.Api.IntegrationTests;

using System.Linq;
using Cluckwork.Infrastructure.Identity;

// #265 review — the break-glass temp-password generator (internal; visible via
// InternalsVisibleTo). Policy compliance is enforced indirectly too (a
// non-compliant password fails ResetPasswordAsync and reddens
// AdminRecoveryServiceTests), but the generator's own guarantees — every
// required class present, no visually ambiguous glyphs — are unit-asserted here.
// No Docker: pure logic.
public sealed class TemporaryPasswordTests
{
    [Fact]
    public void Generate_IsAlwaysPolicyCompliant_AndOmitsAmbiguousGlyphs()
    {
        // 200 draws: the guaranteed-one-per-class construction makes every draw
        // compliant, and enough samples that a stray excluded glyph would surface.
        for (var i = 0; i < 200; i++)
        {
            var pw = TemporaryPassword.Generate();

            Assert.True(pw.Length >= 12, $"too short ({pw.Length}): {pw}");
            Assert.True(pw.Any(char.IsLower), $"no lowercase: {pw}");
            Assert.True(pw.Any(char.IsUpper), $"no uppercase: {pw}");
            Assert.True(pw.Any(char.IsDigit), $"no digit: {pw}");
            Assert.True(pw.Any(c => !char.IsLetterOrDigit(c)), $"no symbol: {pw}");

            // Excluded ambiguous glyphs (0/O, 1/l/I) must never appear.
            foreach (var bad in "0O1lI")
                Assert.DoesNotContain(bad.ToString(), pw);
        }
    }

    [Fact]
    public void Generate_IsUnpredictable_AcrossDraws()
    {
        // A CSPRNG-backed generator must not repeat; 100 draws with any collision
        // would signal a broken/seeded RNG.
        var seen = new HashSet<string>();
        for (var i = 0; i < 100; i++)
            Assert.True(seen.Add(TemporaryPassword.Generate()), "generated a duplicate temporary password");
    }
}
