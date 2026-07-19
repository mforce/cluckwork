namespace Cluckwork.Domain.Auditing;

// Append-only record of a critical change (#93). Domain data, not telemetry
// (tech spec): written by the handler IN THE SAME TRANSACTION as the change it
// records — a rolled-back change leaves no event. There is deliberately no
// mutation surface: no Version, no update, no delete.
public sealed class AuditEvent : AggregateRoot<Guid>
{
    public const int MaxActionLength = 100;
    public const int MaxEntityTypeLength = 100;
    public const int MaxActorEmailLength = 256;
    public const int MaxReasonLength = 500;

    public DateTimeOffset OccurredAtUtc { get; private set; }
    public Guid ActorUserId { get; private set; }
    // Snapshotted: user rows can be renamed/removed later; the trail must not.
    public string ActorEmail { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public Guid EntityId { get; private set; }
    public string? Reason { get; private set; }
    // Plain text, not jsonb — provider-portability rule; only the API reads it.
    public string? DetailsJson { get; private set; }

    private AuditEvent() { }

    public static AuditEvent Create(
        Guid id, Guid accountId, DateTimeOffset occurredAtUtc,
        Guid actorUserId, string actorEmail,
        string action, string entityType, Guid entityId,
        string? reason = null, string? detailsJson = null)
    {
        if (string.IsNullOrWhiteSpace(action) || action.Length > MaxActionLength)
            throw new ArgumentException("A valid action code is required.", nameof(action));
        if (string.IsNullOrWhiteSpace(entityType) || entityType.Length > MaxEntityTypeLength)
            throw new ArgumentException("A valid entity type is required.", nameof(entityType));

        return new AuditEvent
        {
            Id = id, AccountId = accountId,
            OccurredAtUtc = occurredAtUtc,
            ActorUserId = actorUserId,
            ActorEmail = actorEmail.Length > MaxActorEmailLength
                ? actorEmail[..MaxActorEmailLength] : actorEmail,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Reason = string.IsNullOrWhiteSpace(reason)
                ? null
                : reason.Trim().Length > MaxReasonLength
                    ? reason.Trim()[..MaxReasonLength] : reason.Trim(),
            DetailsJson = detailsJson
        };
    }
}
