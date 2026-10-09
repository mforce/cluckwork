namespace Cluckwork.Application.Modules.Insights.Contracts;

public sealed record AuditEventRead(
    Guid Id, DateTimeOffset OccurredAtUtc, string ActorEmail,
    string Action, string EntityType, Guid EntityId,
    string? Reason, string? DetailsJson,
    string? ConnectedAppClientId, string? ConnectedAppName);
