namespace Cluckwork.Application.Features.Customers.UpdateCustomer;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Customers;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

public sealed class UpdateCustomerHandler(
    ICustomerRepository customers,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public async Task<Result> HandleAsync(UpdateCustomerCommand command, CancellationToken ct)
    {
        var customer = await customers.GetByIdAsync(command.CustomerId, ct);
        if (customer is null)
            return Result.Failure(Error.NotFound(nameof(Customer), command.CustomerId));
        if (customer.Version != command.Version)
            return Result.Failure(Error.Conflict(
                "Customer.VersionMismatch", "This customer was changed since you loaded it — reload and retry."));

        var result = customer.Update(command.Name, command.Phone, command.Email, command.Address, command.Note);
        if (result.IsFailure) return result;

        await audit.WriteAsync(AuditActions.CustomerUpdate, nameof(Customer), customer.Id,
            details: new { customer.Name, customer.Phone, customer.Email, customer.Address, customer.Note }, ct: ct);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
