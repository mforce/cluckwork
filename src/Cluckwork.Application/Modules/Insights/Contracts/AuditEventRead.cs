namespace Cluckwork.Application.Modules.Insights.Contracts;

[ModuleContract("Insights")]
public sealed record AuditEventRead(
    Guid Id, DateTimeOffset OccurredAtUtc, string ActorEmail,
    string Action, string EntityType, Guid EntityId,
    string? Reason, string? DetailsJson);
