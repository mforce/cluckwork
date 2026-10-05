using Cluckwork.Application.Modules.Commerce.Contracts;
using Cluckwork.Domain.Modules.Commerce.Sales;

namespace Cluckwork.Application.Modules.Commerce.Customers;

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(Customer entity, CancellationToken ct = default);

    Task<IReadOnlyList<Customer>> ListAsync(int limit, int offset, CancellationToken ct = default);

    // #512 — picker discovery. Same literal search semantics as the flock
    // route: trimmed, case-insensitive substring, `%`/`_`/`\` matched as data,
    // blank meaning unfiltered; the predicate is applied before the window.
    // Tenant isolation stays on the structural global filter (#613) — customers
    // are not flock-scoped.
    Task<IReadOnlyList<Customer>> SearchAsync(
        string? search, int limit, int offset, CancellationToken ct = default);

    // #512 US4 — scoped bulk display names for row projections (T048): one read
    // resolves the distinct customer ids of a whole returned page, so a Sales
    // order names its customer independently of any picker page.
    //
    // ORDINARY FILTERED LINQ over the model's Customers set, so the tenant filter
    // composes here (#613). Not raw SQL, and no hand-written AccountId predicate —
    // which is what makes IgnoreQueryFilters() a detectable mutation on this read.
    // A missing key means the customer is outside this tenant or gone; the caller
    // must fail explicitly rather than substitute an id fragment.
    Task<IReadOnlyDictionary<Guid, CustomerReference>> GetDisplayNamesAsync(
        IReadOnlyCollection<Guid> customerIds, CancellationToken ct = default);
}
