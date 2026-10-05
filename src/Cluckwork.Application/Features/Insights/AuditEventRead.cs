namespace Cluckwork.Application.Features.Insights;

[ModuleContract("Insights")]
public sealed record AuditEventRead(
    Guid Id, DateTimeOffset OccurredAtUtc, string ActorEmail,
    string Action, string EntityType, Guid EntityId,
    string? Reason, string? DetailsJson);
