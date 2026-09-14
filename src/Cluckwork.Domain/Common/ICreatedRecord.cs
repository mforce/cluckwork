namespace Cluckwork.Domain.Common;

public interface ICreatedRecord
{
    DateTimeOffset CreatedAtUtc { get; }
}
