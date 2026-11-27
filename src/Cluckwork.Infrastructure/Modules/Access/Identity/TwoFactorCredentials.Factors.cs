using Cluckwork.Application.Modules.Access.Contracts;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// The kind of proof a second step asks for: an authenticator or recovery code
// needs the app, a passkey needs a passkey.
internal enum FactorKind { Authenticator, Passkey }

// #1285: which proofs a user can give, and whether a challenge still admits
// one. Static, so login and the second step's options route ask the code
// step's own questions without the owner's state.
internal sealed partial class TwoFactorCredentials
{
    private static readonly string[] NoFactors = [];
    private static readonly string[] AuthenticatorOnly = [SecondFactorKinds.Authenticator];
    private static readonly string[] PasskeyOnly = [SecondFactorKinds.Passkey];
    private static readonly string[] BothFactors = [SecondFactorKinds.Authenticator, SecondFactorKinds.Passkey];

    // #1282 decision 7: the proofs a user can give at the second step, the
    // app or a passkey, as the pending 401's secondFactors lists them.
    internal static IReadOnlyList<string> SecondFactorsOf(ApplicationUser user) =>
        (user.TwoFactorEnabled, user.PasskeyCount > 0) switch
        {
            (true, true) => BothFactors,
            (true, false) => AuthenticatorOnly,
            (false, true) => PasskeyOnly,
            (false, false) => NoFactors,
        };

    // Step 1: the user may still answer the proof's challenge with a proof
    // of this kind. The second step's options route asks the same (#1285).
    internal static bool Admits(ApplicationUser user, ProofBinding binding, FactorKind kind) =>
        user.DisabledAt is null && user.CredentialEpoch >= 1
        && (kind == FactorKind.Passkey ? user.PasskeyCount > 0 : user.TwoFactorEnabled)
        && user.CredentialEpoch == binding.CredentialEpoch
        && string.Equals(ProofBinding.HashStamp(user.SecurityStamp), binding.SecurityStampHash, StringComparison.Ordinal)
        && (binding.SignIns is not { } signIns || signIns == user.SecondFactorSignIns);
}
