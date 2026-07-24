namespace Cluckwork.Application.Features.Users;

// #165 — one place for the new-password bound shared by create, admin-set and
// self-service change. Identity enforces the full policy (upper/lower/digit/
// symbol); this length check just yields a clean 400 instead of a round-trip
// that comes back as a provider failure.
public static class PasswordRules
{
    public const int MinLength = 12;
}
