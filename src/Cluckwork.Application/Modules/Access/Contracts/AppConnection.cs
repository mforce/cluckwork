namespace Cluckwork.Application.Modules.Access.Contracts;

// #799 — one person's connection to one app: every valid authorization they approved
// for it, merged. Two approvals can race and leave two (#798), and a wider request adds
// another, so Scopes is their union, ConnectedAtUtc the earliest and LastUsedAtUtc the
// latest. AppName is the app's own self-asserted choice (#797); render it as text.
public sealed record AppConnection(
    Guid UserId,
    string ClientId,
    string? AppName,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ConnectedAtUtc,
    DateTimeOffset? LastUsedAtUtc);
