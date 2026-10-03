using Cluckwork.Application.Features.Catalog;
using Cluckwork.Application.Features.Customers;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

namespace Cluckwork.Application.Tests.Sales;

// #854: the read paths copy each aggregate field by field into positional
// records, where two Guids, two strings or two counts can swap without a
// compile error.
public sealed class CommerceModuleTests
{
    private static readonly Guid AccountId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid FarmId = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid ProductId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid UnmappedProductId = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid GradeId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid ConversionId = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    private static readonly Guid CustomerId = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid OrderId = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid PaymentId = Guid.Parse("00000000-0000-0000-0000-0000000000e2");
    private static readonly DateOnly OrderDate = new(2026, 9, 20);
    private static readonly DateOnly PaymentDate = new(2026, 9, 22);

    private readonly FakeProducts _products = new();
    private readonly FakeConversions _conversions = new();
    private readonly FakeCustomers _customers = new();
    private readonly FakeOrders _orders = new();
    private readonly FakePayments _payments = new();
    private readonly Guid _itemId;

    // The handlers sit on the write paths only; these tests exercise the reads.
    private CommerceModule Module() => new(
        _products, _conversions, _customers, _orders, _payments,
        null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    public CommerceModuleTests()
    {
        var product = Product.Create(
            ProductId, AccountId, FarmId, "Large Eggs", ProductType.Egg, ProductUnit.Dozen, 1_800, "KWD", 3, "Graded A");
        Assert.True(product.Deactivate().IsSuccess);
        _products.Rows.Add(product);
        _products.Rows.Add(Product.Create(
            UnmappedProductId, AccountId, FarmId, "Spent hens", ProductType.LiveBird, ProductUnit.Bird, null, "KWD", 3, null));
        _products.Mappings.Add(ProductEggGradeMapping.Create(Guid.NewGuid(), AccountId, ProductId, GradeId));

        var conversion = EggUnitConversion.Create(ConversionId, AccountId, EggUnit.Tray, 30);
        Assert.True(conversion.Update(24, false).IsSuccess);
        _conversions.Rows.Add(conversion);

        var customer = Customer.Create(CustomerId, AccountId, "Mercado", "555-0100", "orders@mercado.example",
            "12 Market Rd", "Pays cash");
        Assert.True(customer.Update("Mercado Central", "555-0117", "buy@mercado.example", "14 Market Rd", "Pays card")
            .IsSuccess);
        _customers.Rows.Add(customer);

        var order = SalesOrder.Create(OrderId, AccountId, CustomerId, "SO-77", OrderDate, "KWD", 3);
        _itemId = order.AddItem(ProductId, ProductType.Egg, GradeId, ProductUnit.Dozen, 12, 3,
            new Money(1_500, "KWD", 3), 1_800, ListPriceBasis.Recorded).Value.Id;
        Assert.True(order.Confirm(DiscountReasonCode.Volume, "Ten trays a week").IsSuccess);
        Assert.True(order.Void("Entered twice").IsSuccess);
        _orders.Rows.Add(order);

        var payment = Payment.Create(PaymentId, AccountId, OrderId, CustomerId, PaymentDate, 700, "KWD", 3,
            PaymentMethod.Card, "REF-9", "Deposit");
        Assert.True(payment.Void("Bounced").IsSuccess);
        _payments.Rows.Add(payment);
    }

    private SalesOrderDetails ExpectedOrder => new(
        OrderId, CustomerId, "SO-77", OrderDate, SalesOrderStatus.Voided, new Money(4_500, "KWD", 3),
        "Entered twice", [], DiscountReasonCode.Volume, "Ten trays a week");

    private SalesOrderItemDetails ExpectedItem => new(
        _itemId, ProductId, GradeId, ProductUnit.Dozen, 12, 3, 36, new Money(1_500, "KWD", 3), 1_800,
        ListPriceBasis.Recorded);

    private static readonly PaymentDetails ExpectedPayment = new(
        PaymentId, OrderId, CustomerId, PaymentDate, 700, "KWD", 3, PaymentMethod.Card, "REF-9", "Deposit",
        true, "Bounced", 1);

    private void AssertOrder(SalesOrderDetails? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(ExpectedOrder, actual with { Items = [] });
        Assert.Equal([ExpectedItem], actual.Items);
    }

    // A product with no grade mapping answers Guid.Empty, not null, as
    // ProductEndpoints did before the contract.
    [Fact]
    public async Task ListProducts_CopiesEveryFieldAndJoinsTheGradeMapping()
    {
        Assert.Equal(
            [
                new ProductDetails(ProductId, "Large Eggs", ProductType.Egg, ProductUnit.Dozen, 1_800, "KWD", 3,
                    GradeId, "Graded A", false, 1),
                new ProductDetails(UnmappedProductId, "Spent hens", ProductType.LiveBird, ProductUnit.Bird, null,
                    "KWD", 3, Guid.Empty, null, true, 0),
            ],
            await Module().ListProductsAsync(includeInactive: true, default));
        Assert.Equal(["list True", "mappings"], _products.Calls);
    }

    [Fact]
    public async Task ConversionReads_CopyEveryField()
    {
        var expected = new EggUnitConversionDetails(ConversionId, EggUnit.Tray, 24, false, 1);

        Assert.Equal([expected], await Module().ListConversionsAsync(default));
        Assert.Equal(expected, await new EggUnitConversionLookup(_conversions).GetByUnitAsync(EggUnit.Tray, default));
        Assert.Null(await new EggUnitConversionLookup(_conversions).GetByUnitAsync(EggUnit.Case, default));
    }

    [Fact]
    public async Task CustomerReads_CopyEveryFieldAndForwardTheArguments()
    {
        var expected = new CustomerDetails(
            CustomerId, "Mercado Central", "555-0117", "buy@mercado.example", "14 Market Rd", "Pays card", 1);

        Assert.Equal([expected], await Module().SearchCustomersAsync("merc", 25, 50, default));
        Assert.Equal(expected, await Module().GetCustomerAsync(CustomerId, default));
        Assert.Null(await Module().GetCustomerAsync(Guid.NewGuid(), default));
        Assert.Equal(
            new Dictionary<Guid, CustomerReference> { [CustomerId] = new(CustomerId, "Mercado Central") },
            await Module().GetCustomerNamesAsync([CustomerId], default));
        Assert.Equal(["search merc 25 50", $"names {CustomerId}"], _customers.Calls);
    }

    [Fact]
    public async Task OrderReads_CopyEveryFieldAndForwardTheArguments()
    {
        AssertOrder(await Module().GetSalesOrderAsync(OrderId, default));
        Assert.Null(await Module().GetSalesOrderAsync(Guid.NewGuid(), default));

        var filter = new SalesOrderListFilter(SalesOrderStatus.Voided, CustomerId, OrderDate, PaymentDate,
            SettlementScope.UnpaidOnly);
        var row = Assert.Single(await Module().ListSalesOrdersAsync(filter, 10, 20, default));
        AssertOrder(row.Order);
        Assert.Equal(4_200, row.OutstandingMinorUnits);
        Assert.Equal([$"list {filter} 10 20"], _orders.Calls);
    }

    [Fact]
    public async Task PaymentReads_CopyEveryFieldAndForwardTheArguments()
    {
        Assert.Equal(ExpectedPayment, await Module().GetPaymentAsync(PaymentId, default));
        Assert.Null(await Module().GetPaymentAsync(Guid.NewGuid(), default));
        Assert.Equal([ExpectedPayment], await Module().ListOrderPaymentsAsync(OrderId, default));
        Assert.Equal(300, await Module().SumNonVoidedPaymentsAsync(OrderId, default));
        Assert.Equal([new CustomerBalance(CustomerId, 4_500, 300)], await Module().ListCustomerBalancesAsync(default));
        Assert.Equal([$"payments {OrderId}", $"sum {OrderId}", "balances"], _payments.Calls);
    }

    private sealed class FakeProducts : IProductRepository
    {
        public List<Product> Rows { get; } = [];
        public List<ProductEggGradeMapping> Mappings { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<Product>> ListAsync(bool includeInactive, CancellationToken ct = default)
        {
            Calls.Add($"list {includeInactive}");
            return Task.FromResult<IReadOnlyList<Product>>(Rows);
        }

        public Task<IReadOnlyList<ProductEggGradeMapping>> ListMappingsAsync(CancellationToken ct = default)
        {
            Calls.Add("mappings");
            return Task.FromResult<IReadOnlyList<ProductEggGradeMapping>>(Mappings);
        }

        public Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAsync(Product product, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProductEggGradeMapping?> GetMappingAsync(Guid productId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddMappingAsync(ProductEggGradeMapping mapping, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeConversions : IEggUnitConversionRepository
    {
        public List<EggUnitConversion> Rows { get; } = [];

        public Task<IReadOnlyList<EggUnitConversion>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EggUnitConversion>>(Rows);

        public Task<EggUnitConversion?> GetByUnitAsync(EggUnit unit, CancellationToken ct = default) =>
            Task.FromResult(Rows.SingleOrDefault(c => c.UnitCode == unit));

        public Task<EggUnitConversion?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCustomers : ICustomerRepository
    {
        public List<Customer> Rows { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<Customer>> SearchAsync(
            string? search, int limit, int offset, CancellationToken ct = default)
        {
            Calls.Add($"search {search} {limit} {offset}");
            return Task.FromResult<IReadOnlyList<Customer>>(Rows);
        }

        public Task<Customer?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Rows.SingleOrDefault(c => c.Id == id));

        public Task<IReadOnlyDictionary<Guid, CustomerReference>> GetDisplayNamesAsync(
            IReadOnlyCollection<Guid> customerIds, CancellationToken ct = default)
        {
            Calls.Add($"names {string.Join(",", customerIds)}");
            return Task.FromResult<IReadOnlyDictionary<Guid, CustomerReference>>(Rows
                .Where(c => customerIds.Contains(c.Id)).ToDictionary(c => c.Id, c => new CustomerReference(c.Id, c.Name)));
        }

        public Task<IReadOnlyList<Customer>> ListAsync(int limit, int offset, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAsync(Customer entity, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeOrders : ISalesOrderRepository
    {
        public List<SalesOrder> Rows { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<SalesOrder?> GetReadOnlyAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Rows.SingleOrDefault(o => o.Id == id));

        public Task<IReadOnlyList<SalesOrderListRow>> ListAsync(
            SalesOrderListFilter filter, int limit, int offset, CancellationToken ct = default)
        {
            Calls.Add($"list {filter} {limit} {offset}");
            return Task.FromResult<IReadOnlyList<SalesOrderListRow>>(
                Rows.Select(o => new SalesOrderListRow(o, 4_200)).ToList());
        }

        public Task<SalesOrder?> GetByIdLockedAsync(Guid accountId, Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SalesOrder?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(SalesOrder entity, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakePayments : IPaymentRepository
    {
        public List<Payment> Rows { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Rows.SingleOrDefault(p => p.Id == id));

        public Task<IReadOnlyList<Payment>> ListByOrderAsync(Guid salesOrderId, CancellationToken ct = default)
        {
            Calls.Add($"payments {salesOrderId}");
            return Task.FromResult<IReadOnlyList<Payment>>(Rows.Where(p => p.SalesOrderId == salesOrderId).ToList());
        }

        public Task<long> SumNonVoidedByOrderAsync(Guid salesOrderId, CancellationToken ct = default)
        {
            Calls.Add($"sum {salesOrderId}");
            return Task.FromResult(300L);
        }

        public Task<IReadOnlyList<CustomerBalance>> ListCustomerBalancesAsync(CancellationToken ct = default)
        {
            Calls.Add("balances");
            return Task.FromResult<IReadOnlyList<CustomerBalance>>([new CustomerBalance(CustomerId, 4_500, 300)]);
        }

        public Task<bool> AnyNonVoidedByOrderAsync(Guid salesOrderId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAsync(Payment entity, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
