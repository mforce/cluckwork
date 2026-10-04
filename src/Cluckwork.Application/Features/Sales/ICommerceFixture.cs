namespace Cluckwork.Application.Features.Sales;

// Simulation fixture reads; registered beside the seeders outside Production.
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
}

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
