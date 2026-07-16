namespace Cluckwork.Application.Features.Sales.CreateSalesOrder;

using FluentValidation;

public sealed class CreateSalesOrderValidator : AbstractValidator<CreateSalesOrderCommand>
{
    public CreateSalesOrderValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.OrderDate).NotEmpty();
    }
}
