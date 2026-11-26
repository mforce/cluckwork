using Cluckwork.Application.Modules.Access.Contracts;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1285: which proofs a user can give, and whether a challenge still admits
// one. Static, so login and the second step's options route ask the code
// step's own questions without the owner's state.
internal sealed partial class TwoFactorCredentials
{
    // #1282 decision 7: the proofs a user can give at the second step, the
    // app or a passkey; a recovery code counts as the app.
    internal static List<string> SecondFactorsOf(ApplicationUser user)
    {
        List<string> factors = [];
        if (user.TwoFactorEnabled) factors.Add(SecondFactorKinds.Authenticator);
        if (user.PasskeyCount > 0) factors.Add(SecondFactorKinds.Passkey);
        return factors;
    }

    // Step 1: the user may still answer the proof's challenge with a proof
    // of this kind. The second step's options route asks the same (#1285).
    internal static bool Admits(ApplicationUser user, ProofBinding binding, string kind) =>
        user.DisabledAt is null && user.CredentialEpoch >= 1 && SecondFactorsOf(user).Contains(kind)
        && user.CredentialEpoch == binding.CredentialEpoch
        && string.Equals(ProofBinding.HashStamp(user.SecurityStamp), binding.SecurityStampHash, StringComparison.Ordinal)
        && (binding.SignIns is not { } signIns || signIns == user.SecondFactorSignIns);
}
