namespace Cluckwork.Application.Features.Accounts;

// Simulation fixture reads and writes; registered beside the seeders outside Production.
public interface IFarmFixture
{
    // Reads every farm, ignoring the tenant filter.
    Task<bool> AccountExistsAsync(Guid accountId, CancellationToken ct = default);

    // Counts every farm, ignoring the tenant filter.
    Task<int> CountAccountsAsync(CancellationToken ct = default);

    // Resolve this port from a scope whose tenant is already accountId: the
    // tenant stamp refuses a farm row written under another farm's tenant.
    Task CreateAccountAsync(
        Guid accountId, string name, string slug, string timeZoneId, string currencyCode,
        CancellationToken ct = default);
}
