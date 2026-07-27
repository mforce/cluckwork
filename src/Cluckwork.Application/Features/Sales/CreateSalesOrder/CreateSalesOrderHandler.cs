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
        // default currency is the single-farm MVP stand-in. A resolved tenant
        // without an account row is an invariant violation — fail, don't guess.
        // Snapshot and insert share a transaction with FOR SHARE on the
        // account row (#162), so the order can never land in a denomination
        // the farm is mid-flight out of.
        Result<Guid>? outcome = null;
        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            var account = await accounts.GetCurrentSharedLockedAsync(transactionCt);
            if (account is null)
            {
                outcome = Result.Failure<Guid>(Error.NotFound("Account", accountId));
                return false;
            }

            var orderId = Guid.NewGuid();
            var reference = $"SO-{orderId.ToString("N")[..8].ToUpperInvariant()}";
            var order = SalesOrder.Create(
                orderId, accountId, command.CustomerId, reference, command.OrderDate,
                account.DefaultCurrencyCode, account.DefaultCurrencyMinorUnit);

            await orders.AddAsync(order, transactionCt);
            outcome = Result.Success(order.Id);
            return true;
        }, ct);

        return outcome!;
    }
}
