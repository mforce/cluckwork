namespace Cluckwork.Domain.Catalog;

using Cluckwork.Domain.Common;

// Spec §10.2 — egg products map to egg grades; part 2 allocates a sold line's
// eggs from lots of the mapped grade. Phase 1 enforces exactly ONE mapping per
// product (unique index on ProductId); multi-grade products (mixed cartons)
// arrive with a later slice, which is why this is a table and not a column.
public sealed class ProductEggGradeMapping : Entity<Guid>
{
    public Guid ProductId { get; private set; }
    public Guid EggGradeId { get; private set; }

    private ProductEggGradeMapping() { }

    public static ProductEggGradeMapping Create(Guid id, Guid accountId, Guid productId, Guid eggGradeId)
        => new() { Id = id, AccountId = accountId, ProductId = productId, EggGradeId = eggGradeId };

    // Re-pointing an egg product at a different grade is allowed: sold lines
    // will snapshot their allocation at sale time (part 2), so history never
    // reinterprets.
    public void Repoint(Guid eggGradeId) => EggGradeId = eggGradeId;
}
