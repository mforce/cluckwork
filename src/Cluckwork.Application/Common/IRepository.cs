namespace Cluckwork.Application.Common;

public interface IRepository<T, TId>
    where T : class
    where TId : notnull
{
    Task<T?> GetByIdAsync(TId id, CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    void Update(T entity);
    void Remove(T entity);
}
