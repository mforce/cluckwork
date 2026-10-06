namespace Cluckwork.Application.Modules.Access.Contracts;

// #356 — DisabledAt is null for an active user. Exposed on the LIST rather
// than filtered out of it: an Owner cannot re-enable someone they cannot see.
public sealed record UserSummary(
    Guid Id, string Email, string? DisplayName, string Role, DateTimeOffset? DisabledAt);
