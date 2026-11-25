namespace Cluckwork.Application.Modules.Access.Contracts;

// #1284 (record 1282 "What a caller sees"): what the /api/v1/auth/passkeys
// routes hand back. Ceremony is the passkey_ceremonies row's id; OptionsJson is
// Identity's PublicKeyCredentialCreationOptions, passed to the browser as is.
public sealed record PasskeyCeremonyStart(Guid Ceremony, string OptionsJson);

// Id is the credential id, base64url. Address is the origin the passkey was
// made at, set only when two or more origins are configured (decision 14).
public sealed record PasskeySummary(string Id, string? Name, DateTimeOffset CreatedAt, bool Synced, string? Address);

// Enabled: this address runs passkey ceremonies. The list still shows every
// passkey while they are off, for removal.
public sealed record PasskeyList(bool Enabled, IReadOnlyList<PasskeySummary> Passkeys);

// Tokens is set when the passkey was the user's first factor, which ended
// every other session (decision 16).
public sealed record PasskeyRegistration(PasskeySummary Passkey, TokenPair? Tokens)
{
    public override string ToString() => "[redacted]";
}

// The grant is a credential, so the record never prints it.
public sealed record RemoveUserPasskeysCommand(Guid UserId, string? StepUpToken)
{
    public override string ToString() => "[redacted]";
}

public static class PasskeyErrorCodes
{
    // Passkeys:Origins is empty, or the request's host is no configured origin's host. 404.
    public const string Off = "Passkeys.Off";
    // Any refused ceremony or assertion: one body, the reason only in a debug log.
    public const string Refused = "Auth.PasskeyRefused";
    // A sixth passkey. 409.
    public const string LimitReached = "Auth.PasskeyLimitReached";
    public const string NotFound = "Passkeys.NotFound";
}
