using Cluckwork.Application.Modules.Commerce.Contracts;
using FluentValidation;

namespace Cluckwork.Application.Modules.Commerce.Sales.CreateSalesOrder;

public sealed class CreateSalesOrderValidator : AbstractValidator<CreateSalesOrderCommand>
{
    public CreateSalesOrderValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty().WithErrorCode("SalesOrder.CustomerId.Required");
        RuleFor(x => x.OrderDate).NotEmpty().WithErrorCode("SalesOrder.OrderDate.Required");
    }
}
