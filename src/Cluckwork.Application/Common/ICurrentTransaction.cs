namespace Cluckwork.Application.Common;

// The id of the transaction the current unit of work runs in, or null outside
// one. Row locks last only as long as their transaction, so a port that hands
// out locked state checks that the same transaction is still current (#854).
public interface ICurrentTransaction
{
    Guid? Id { get; }
}
