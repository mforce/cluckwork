namespace Cluckwork.Application.Features.Sales.CreateSalesOrder;

using FluentValidation;

public sealed class CreateSalesOrderValidator : AbstractValidator<CreateSalesOrderCommand>
{
    public CreateSalesOrderValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty().WithErrorCode("SalesOrder.CustomerId.Required");
        RuleFor(x => x.OrderDate).NotEmpty().WithErrorCode("SalesOrder.OrderDate.Required");
    }
}
