namespace Cluckwork.Application.Features.Sales.CreateSalesOrder;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.Customers;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

public sealed class CreateSalesOrderHandler(
    ICustomerRepository customers,
    IAccountRepository accounts,
    IUnitOfWork unitOfWork,
    ISalesOrderRepository orders)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateSalesOrderCommand command, Guid accountId, CancellationToken ct)
    {
        // Tenant query filter scopes the lookup — a foreign customer reads as null.
        var customer = await customers.GetByIdAsync(command.CustomerId, ct);
        if (customer is null)
            return Result.Failure<Guid>(Error.NotFound(nameof(Customer), command.CustomerId));

        // Spec: orders snapshot the farm's currency at creation. The account's
        // default currency is the single-farm MVP stand-in.
        var account = await accounts.GetCurrentAsync(ct);
        var currency = account?.DefaultCurrencyCode ?? "USD";

        var orderId = Guid.NewGuid();
        var reference = $"SO-{orderId.ToString("N")[..8].ToUpperInvariant()}";
        var order = SalesOrder.Create(
            orderId, accountId, command.CustomerId, reference, command.OrderDate, currency);

        await orders.AddAsync(order, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(order.Id);
    }
}
