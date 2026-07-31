namespace Cluckwork.Application.Features.Users;

// #165 — one place for the new-password bound shared by create, admin-set and
// self-service change. Identity enforces the full policy (upper/lower/digit/
// symbol); this length check just yields a clean 400 instead of a round-trip
// that comes back as a provider failure.
public static class PasswordRules
{
    public const int MinLength = 12;

    // #309 — upper bound on any credential field. PBKDF2 hashes the FULL input on
    // every verify — including the unknown-user timing-equalization hash — so an
    // unbounded password is a CPU/memory amplifier an attacker controls. 256 sits
    // far above the 12-char minimum and is generous for a passphrase, while
    // capping the work one request can force. Enforced in the credential
    // validators, ahead of the hasher.
    public const int MaxLength = 256;
}
