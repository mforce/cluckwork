namespace Cluckwork.Domain.Common;

public interface IMutableRecord : ICreatedRecord
{
    DateTimeOffset UpdatedAtUtc { get; }
}
