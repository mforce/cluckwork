namespace Cluckwork.Application.Features.Accounts;

using Cluckwork.Domain.Accounts;

public interface IAccountRepository
{
    // The current tenant's account (query-filter scoped).
    Task<Account?> GetCurrentAsync(CancellationToken ct = default);
}
