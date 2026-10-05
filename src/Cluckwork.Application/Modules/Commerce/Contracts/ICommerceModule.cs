using Cluckwork.Domain.Modules.Commerce.Contracts;
using Cluckwork.Domain.Modules.Commerce.Sales;

namespace Cluckwork.Application.Modules.Commerce.Contracts;

// #854: the Commerce contract for adapters. Adapters reach Commerce only
// through the types in this Contracts folder;
// peer modules use the narrower IEggUnitConversionLookup port.
public interface ICommerceModule
{
    // The EntityType Commerce writes on a sales order's audit rows; provenance reads key by it.
    const string SalesOrderAuditEntityType = nameof(SalesOrder);

    Task<IReadOnlyList<ProductDetails>> ListProductsAsync(bool includeInactive, CancellationToken ct);

    Task<Result<Guid>> CreateProductAsync(CreateProductCommand command, Guid accountId, CancellationToken ct);

    Task<Result> UpdateProductAsync(UpdateProductCommand command, CancellationToken ct);

    Task<Result> SetProductActiveAsync(Guid id, bool active, CancellationToken ct);

    Task<IReadOnlyList<EggUnitConversionDetails>> ListConversionsAsync(CancellationToken ct);

    Task<Result> UpdateConversionAsync(UpdateEggUnitConversionCommand command, CancellationToken ct);

    Task<Result<Guid>> CreateCustomerAsync(CreateCustomerCommand command, Guid accountId, CancellationToken ct);

    Task<Result> UpdateCustomerAsync(UpdateCustomerCommand command, CancellationToken ct);

    Task<IReadOnlyList<CustomerDetails>> SearchCustomersAsync(
        string? search, int limit, int offset, CancellationToken ct);

    Task<CustomerDetails?> GetCustomerAsync(Guid id, CancellationToken ct);

    // A missing key is a customer outside the tenant or gone.
    Task<IReadOnlyDictionary<Guid, CustomerReference>> GetCustomerNamesAsync(
        IReadOnlyCollection<Guid> customerIds, CancellationToken ct);

    Task<Result<Guid>> CreateSalesOrderAsync(CreateSalesOrderCommand command, Guid accountId, CancellationToken ct);

    Task<Result<Guid>> AddOrderItemAsync(AddOrderItemCommand command, Guid accountId, CancellationToken ct);

    Task<Result> UpdateOrderItemAsync(UpdateOrderItemCommand command, CancellationToken ct);

    Task<Result> RemoveOrderItemAsync(Guid orderId, Guid itemId, CancellationToken ct);

    Task<Result> CancelSalesOrderAsync(Guid orderId, CancellationToken ct);

    Task<Result<ConfirmSaleResponse>> ConfirmSaleAsync(
        ConfirmSaleCommand command, Guid accountId, Guid actingUserId, CancellationToken ct);

    Task<Result<VoidSaleResponse>> VoidSaleAsync(VoidSaleCommand command, Guid accountId, CancellationToken ct);

    Task<SalesOrderDetails?> GetSalesOrderAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<SalesOrderListItem>> ListSalesOrdersAsync(
        SalesOrderListFilter filter, int limit, int offset, CancellationToken ct);

    Task<Result<Guid>> RecordPaymentAsync(RecordPaymentCommand command, Guid accountId, CancellationToken ct);

    Task<Result> VoidPaymentAsync(VoidPaymentCommand command, CancellationToken ct);

    Task<PaymentDetails?> GetPaymentAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<PaymentDetails>> ListOrderPaymentsAsync(Guid salesOrderId, CancellationToken ct);

    Task<long> SumNonVoidedPaymentsAsync(Guid salesOrderId, CancellationToken ct);

    Task<IReadOnlyList<CustomerBalance>> ListCustomerBalancesAsync(CancellationToken ct);
}

public sealed record ProductDetails(
    Guid Id, string Name, ProductType ProductType, ProductUnit DefaultUnit, long? DefaultPriceMinorUnits,
    string CurrencyCode, int CurrencyMinorUnit, Guid? EggGradeId, string? Notes, bool Active, int Version);

public sealed record CustomerDetails(
    Guid Id, string Name, string Phone, string? Email, string? Address, string? Note, int Version);

public sealed record SalesOrderDetails(
    Guid Id, Guid CustomerId, string ReferenceNumber, DateOnly OrderDate, SalesOrderStatus Status,
    Money TotalAmount, string? VoidReason, IReadOnlyList<SalesOrderItemDetails> Items,
    DiscountReasonCode? DiscountReasonCode, string? DiscountReasonNote);

public sealed record SalesOrderItemDetails(
    Guid Id, Guid ProductId, Guid EggGradeId, ProductUnit Unit, int BaseUnitFactor, int Quantity,
    int QuantityBase, Money UnitPrice, long? ListUnitPriceMinorUnits, ListPriceBasis ListPriceBasis);

// What the order still owes; see SalesOrderListRow.
public sealed record SalesOrderListItem(SalesOrderDetails Order, long? OutstandingMinorUnits);

public sealed record PaymentDetails(
    Guid Id, Guid SalesOrderId, Guid CustomerId, DateOnly PaymentDate, long AmountMinorUnits,
    string CurrencyCode, int CurrencyMinorUnit, PaymentMethod Method, string? ReferenceNumber,
    string? Note, bool Voided, string? VoidReason, int Version);
