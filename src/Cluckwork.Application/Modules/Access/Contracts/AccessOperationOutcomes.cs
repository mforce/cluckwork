namespace Cluckwork.Application.Modules.Access.Contracts;

// #589 — Slug is NULLABLE and sits beside the other nullable fields deliberately.
// This record is not a mirror of AccountProvisionOutcome: that one has no no-op
// path, so all its fields are non-null, whereas AlreadyProvisioned() here returns
// a value with nothing populated. Copying its non-nullable `string Slug` across
// would make the idempotent branch unrepresentable.
[ModuleContract("Access")]
public sealed record FirstRunAdminOutcome(
    bool WasAlreadyProvisioned,
    string? Email,
    Guid? AccountId,
    string? Slug,
    string? TemporaryPassword)
{
    public static FirstRunAdminOutcome AlreadyProvisioned() => new(true, null, null, null, null);

    public static FirstRunAdminOutcome Provisioned(
        string email, Guid accountId, string slug, string password) =>
        new(false, email, accountId, slug, password);
}

// #589 — Slug is a plain NON-NULLABLE string, unlike FirstRunAdminOutcome's
// nullable one. That record has a no-op path (AlreadyProvisioned returns a
// value with nothing populated) so every field must be nullable there;
// AdminRecoveryResult has no no-op path — recovery always ran — so every field
// is populated and none are nullable. The slug is read off `lockedAccount`
// (already loaded FOR UPDATE in the transaction, no new query) and printed by
// recover-admin because #532 made the farm code a required login input.
[ModuleContract("Access")]
public sealed record AdminRecoveryResult(string Email, Guid AccountId, string Slug, string TemporaryPassword);

// Changed = "this command transitioned the farm", so a verb can report a no-op
// re-run without going back to the database to work out what it did.
[ModuleContract("Access")]
public sealed record AccountLifecycleOutcome(bool Changed);

// Changed = "this command changed the code", so the verb can tell an operator their
// re-run was a no-op without re-reading the database. Deliberately NOT "the farm is fine".
[ModuleContract("Access")]
public sealed record AccountRenameOutcome(bool Changed);

[ModuleContract("Access")]
public sealed record AccountProvisionOutcome(
    Guid AccountId,
    string Slug,
    string OwnerEmail,
    string TemporaryPassword);
