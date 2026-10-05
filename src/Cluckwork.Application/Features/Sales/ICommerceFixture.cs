namespace Cluckwork.Application.Features.Sales;

// Seed fixture reads and writes; registered beside the seeders outside Production.
[ModuleContract("Commerce")]
public interface ICommerceFixture
{
    Task<Guid?> FindProductIdByNameAsync(string name, CancellationToken ct = default);

    Task<Guid?> FindCustomerIdByNameAsync(string name, CancellationToken ct = default);

    Task<Guid?> FindOrderIdAsync(Guid customerId, DateOnly orderDate, CancellationToken ct = default);

    Task<bool> OrderHasLineAsync(Guid orderId, Guid productId, CancellationToken ct = default);

    Task<bool> IsOrderConfirmedAsync(Guid orderId, CancellationToken ct = default);

    Task<bool> PaymentExistsAsync(Guid orderId, CancellationToken ct = default);

    Task<long> GetOrderTotalMinorUnitsAsync(Guid orderId, CancellationToken ct = default);

    Task<CommerceFixtureCounts> CountAsync(CancellationToken ct = default);

    Task<bool> AnyCustomerAsync(CancellationToken ct = default);

    // Demo cleanup: deletes every row of the farm, ignoring the tenant filter.
    // Runs inside the caller's transaction and never commits.
    Task PurgeOrdersAndCustomersAsync(Guid accountId, CancellationToken ct = default);
}

[ModuleContract("Commerce")]
public sealed record CommerceFixtureCounts(
    int Customers,
    int SalesOrders,
    int DraftOrders,
    int ConfirmedOrders,
    int ShippedOrders,
    int InvoicedOrders,
    int CancelledOrders,
    int VoidedOrders,
    int Payments);
