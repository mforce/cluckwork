namespace Cluckwork.Application.Features.Customers.CreateCustomer;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Customers;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

public sealed class CreateCustomerHandler(
    ICustomerRepository customers,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateCustomerCommand command, Guid accountId, CancellationToken ct)
    {
        var customer = Customer.Create(
            Guid.NewGuid(), accountId,
            command.Name, command.Phone, command.Email, command.Address, command.Note);

        await customers.AddAsync(customer, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(customer.Id);
    }
}
