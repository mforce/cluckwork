namespace Cluckwork.Application.Modules.Access.Contracts;

// #165 — self-service: any signed-in user changes their OWN password by proving
// the current one. On success every session is revoked and the caller is handed
// a fresh pair, so other devices are signed out but this one stays in. An
// enrolled user also gives an authenticator or recovery code (#1274 decision 11).
// Every member is a credential, so the record never prints them.
public sealed record ChangeOwnPasswordCommand(string CurrentPassword, string NewPassword, string? Code = null)
{
    public override string ToString() => "[redacted]";
}
