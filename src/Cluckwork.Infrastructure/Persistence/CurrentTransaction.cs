using Cluckwork.Application.Common;

namespace Cluckwork.Infrastructure.Persistence;

public sealed class CurrentTransaction(AppDbContext db) : ICurrentTransaction
{
    public Guid? Id => db.Database.CurrentTransaction?.TransactionId;
}
