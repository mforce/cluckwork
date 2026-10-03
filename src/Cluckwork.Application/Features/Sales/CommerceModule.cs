using Cluckwork.Application.Features.Catalog;
using Cluckwork.Application.Features.Catalog.CreateProduct;
using Cluckwork.Application.Features.Catalog.SetProductActive;
using Cluckwork.Application.Features.Catalog.UpdateEggUnitConversion;
using Cluckwork.Application.Features.Catalog.UpdateProduct;
using Cluckwork.Application.Features.Customers;
using Cluckwork.Application.Features.Customers.CreateCustomer;
using Cluckwork.Application.Features.Customers.UpdateCustomer;
using Cluckwork.Application.Features.Sales.AddOrderItem;
using Cluckwork.Application.Features.Sales.CancelSalesOrder;
using Cluckwork.Application.Features.Sales.ConfirmSale;
using Cluckwork.Application.Features.Sales.CreateSalesOrder;
using Cluckwork.Application.Features.Sales.RecordPayment;
using Cluckwork.Application.Features.Sales.RemoveOrderItem;
using Cluckwork.Application.Features.Sales.UpdateOrderItem;
using Cluckwork.Application.Features.Sales.VoidPayment;
using Cluckwork.Application.Features.Sales.VoidSale;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Sales;

namespace Cluckwork.Application.Features.Sales;

public sealed class CommerceModule(
    IProductRepository products,
    IEggUnitConversionRepository conversions,
    ICustomerRepository customers,
    ISalesOrderRepository orders,
    IPaymentRepository payments,
    CreateProductHandler createProduct,
    UpdateProductHandler updateProduct,
    SetProductActiveHandler setProductActive,
    UpdateEggUnitConversionHandler updateConversion,
    CreateCustomerHandler createCustomer,
    UpdateCustomerHandler updateCustomer,
    CreateSalesOrderHandler createOrder,
    AddOrderItemHandler addItem,
    UpdateOrderItemHandler updateItem,
    RemoveOrderItemHandler removeItem,
    CancelSalesOrderHandler cancelOrder,
    ConfirmSaleHandler confirmSale,
    VoidSaleHandler voidSale,
    RecordPaymentHandler recordPayment,
    VoidPaymentHandler voidPayment) : ICommerceModule
{
    public async Task<IReadOnlyList<ProductDetails>> ListProductsAsync(bool includeInactive, CancellationToken ct)
    {
        var list = await products.ListAsync(includeInactive, ct);
        var grades = (await products.ListMappingsAsync(ct)).ToDictionary(m => m.ProductId, m => m.EggGradeId);
        return list.Select(p => new ProductDetails(
            p.Id, p.Name, p.ProductType, p.DefaultUnit, p.DefaultPriceMinorUnits, p.CurrencyCode,
            p.CurrencyMinorUnit, grades.GetValueOrDefault(p.Id), p.Notes, p.Active, p.Version)).ToList();
    }

    public Task<Result<Guid>> CreateProductAsync(CreateProductCommand command, Guid accountId, CancellationToken ct) =>
        createProduct.HandleAsync(command, accountId, ct);

    public Task<Result> UpdateProductAsync(UpdateProductCommand command, CancellationToken ct) =>
        updateProduct.HandleAsync(command, ct);

    public Task<Result> SetProductActiveAsync(Guid id, bool active, CancellationToken ct) =>
        setProductActive.HandleAsync(id, active, ct);

    public async Task<IReadOnlyList<EggUnitConversionDetails>> ListConversionsAsync(CancellationToken ct) =>
        (await conversions.ListAsync(ct)).Select(EggUnitConversionLookup.ToDetails).ToList();

    public Task<Result> UpdateConversionAsync(UpdateEggUnitConversionCommand command, CancellationToken ct) =>
        updateConversion.HandleAsync(command, ct);

    public Task<Result<Guid>> CreateCustomerAsync(CreateCustomerCommand command, Guid accountId, CancellationToken ct) =>
        createCustomer.HandleAsync(command, accountId, ct);

    public Task<Result> UpdateCustomerAsync(UpdateCustomerCommand command, CancellationToken ct) =>
        updateCustomer.HandleAsync(command, ct);

    public async Task<IReadOnlyList<CustomerDetails>> SearchCustomersAsync(
        string? search, int limit, int offset, CancellationToken ct) =>
        (await customers.SearchAsync(search, limit, offset, ct)).Select(ToDetails).ToList();

    public async Task<CustomerDetails?> GetCustomerAsync(Guid id, CancellationToken ct) =>
        await customers.GetByIdAsync(id, ct) is { } customer ? ToDetails(customer) : null;

    public Task<IReadOnlyDictionary<Guid, CustomerReference>> GetCustomerNamesAsync(
        IReadOnlyCollection<Guid> customerIds, CancellationToken ct) =>
        customers.GetDisplayNamesAsync(customerIds, ct);

    public Task<Result<Guid>> CreateSalesOrderAsync(CreateSalesOrderCommand command, Guid accountId, CancellationToken ct) =>
        createOrder.HandleAsync(command, accountId, ct);

    public Task<Result<Guid>> AddOrderItemAsync(AddOrderItemCommand command, Guid accountId, CancellationToken ct) =>
        addItem.HandleAsync(command, accountId, ct);

    public Task<Result> UpdateOrderItemAsync(UpdateOrderItemCommand command, CancellationToken ct) =>
        updateItem.HandleAsync(command, ct);

    public Task<Result> RemoveOrderItemAsync(Guid orderId, Guid itemId, CancellationToken ct) =>
        removeItem.HandleAsync(orderId, itemId, ct);

    public Task<Result> CancelSalesOrderAsync(Guid orderId, CancellationToken ct) =>
        cancelOrder.HandleAsync(orderId, ct);

    public Task<Result<ConfirmSaleResponse>> ConfirmSaleAsync(
        ConfirmSaleCommand command, Guid accountId, Guid actingUserId, CancellationToken ct) =>
        confirmSale.HandleAsync(command, accountId, actingUserId, ct);

    public Task<Result<VoidSaleResponse>> VoidSaleAsync(VoidSaleCommand command, Guid accountId, CancellationToken ct) =>
        voidSale.HandleAsync(command, accountId, ct);

    public async Task<SalesOrderDetails?> GetSalesOrderAsync(Guid id, CancellationToken ct) =>
        await orders.GetReadOnlyAsync(id, ct) is { } order ? ToDetails(order) : null;

    public async Task<IReadOnlyList<SalesOrderListItem>> ListSalesOrdersAsync(
        SalesOrderListFilter filter, int limit, int offset, CancellationToken ct) =>
        (await orders.ListAsync(filter, limit, offset, ct))
            .Select(r => new SalesOrderListItem(ToDetails(r.Order), r.OutstandingMinorUnits)).ToList();

    public Task<Result<Guid>> RecordPaymentAsync(RecordPaymentCommand command, Guid accountId, CancellationToken ct) =>
        recordPayment.HandleAsync(command, accountId, ct);

    public Task<Result> VoidPaymentAsync(VoidPaymentCommand command, CancellationToken ct) =>
        voidPayment.HandleAsync(command, ct);

    public async Task<PaymentDetails?> GetPaymentAsync(Guid id, CancellationToken ct) =>
        await payments.GetByIdAsync(id, ct) is { } payment ? ToDetails(payment) : null;

    public async Task<IReadOnlyList<PaymentDetails>> ListOrderPaymentsAsync(Guid salesOrderId, CancellationToken ct) =>
        (await payments.ListByOrderAsync(salesOrderId, ct)).Select(ToDetails).ToList();

    public Task<long> SumNonVoidedPaymentsAsync(Guid salesOrderId, CancellationToken ct) =>
        payments.SumNonVoidedByOrderAsync(salesOrderId, ct);

    public Task<IReadOnlyList<CustomerBalance>> ListCustomerBalancesAsync(CancellationToken ct) =>
        payments.ListCustomerBalancesAsync(ct);

    private static CustomerDetails ToDetails(Customer c) =>
        new(c.Id, c.Name, c.Phone, c.Email, c.Address, c.Note, c.Version);

    private static SalesOrderDetails ToDetails(SalesOrder o) =>
        new(o.Id, o.CustomerId, o.ReferenceNumber, o.OrderDate, o.Status, o.TotalAmount, o.VoidReason,
            o.Items.Select(i => new SalesOrderItemDetails(
                i.Id, i.ProductId, i.EggGradeId, i.Unit, i.BaseUnitFactor, i.Quantity, i.QuantityBase,
                i.UnitPrice, i.ListUnitPriceMinorUnits, i.ListPriceBasis)).ToList(),
            o.DiscountReasonCode, o.DiscountReasonNote);

    private static PaymentDetails ToDetails(Payment p) =>
        new(p.Id, p.SalesOrderId, p.CustomerId, p.PaymentDate, p.AmountMinorUnits, p.CurrencyCode,
            p.CurrencyMinorUnit, p.Method, p.ReferenceNumber, p.Note, p.Voided, p.VoidReason, p.Version);
}
