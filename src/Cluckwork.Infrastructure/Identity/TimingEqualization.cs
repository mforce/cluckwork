namespace Cluckwork.Infrastructure.Identity;

using Microsoft.AspNetCore.Identity;

// Shared PBKDF2 timing-equalization hash (#128 / #308): verifying an
// incoming password against this costs the same as a real password check, so
// a branch that skips the real check (unknown user, wrong account) doesn't
// return measurably faster and leak which branch fired. Shared by
// IdentityProvider.LoginAsync and StepUpGrantService.IssueAsync — two
// independent password-verification entry points with the same timing-oracle
// risk.
internal static class TimingEqualization
{
    public static readonly string DummyHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!9x");
}
