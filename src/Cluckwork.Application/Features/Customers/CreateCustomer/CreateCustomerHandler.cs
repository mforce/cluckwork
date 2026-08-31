namespace Cluckwork.Application.Features.Customers.CreateCustomer;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Customers;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

public sealed class CreateCustomerHandler(
    ICustomerRepository customers,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateCustomerCommand command, Guid accountId, CancellationToken ct)
    {
        var customer = Customer.Create(
            Guid.NewGuid(), accountId,
            command.Name, command.Phone, command.Email, command.Address, command.Note);

        await customers.AddAsync(customer, ct);
        await audit.WriteAsync(AuditActions.CustomerCreate, nameof(Customer), customer.Id,
            details: new { customer.Name, customer.Phone, customer.Email, customer.Address, customer.Note }, ct: ct);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(customer.Id);
    }
}
