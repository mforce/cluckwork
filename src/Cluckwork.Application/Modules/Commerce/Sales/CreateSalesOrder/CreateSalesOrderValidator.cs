using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Commerce.Contracts;
using FluentValidation;

namespace Cluckwork.Application.Modules.Commerce.Sales.CreateSalesOrder;

public sealed class CreateSalesOrderValidator : AbstractValidator<CreateSalesOrderCommand>
{
    public CreateSalesOrderValidator(IFarmClock farmClock)
    {
        RuleFor(x => x.CustomerId).NotEmpty().WithErrorCode("SalesOrder.CustomerId.Required");
        RuleFor(x => x.OrderDate)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode("SalesOrder.OrderDate.Required")
            // The farm's own today (#35), as RecordPaymentValidator reads it.
            .MustAsync(async (d, ct) => d <= await farmClock.TodayAsync(ct))
            .WithMessage("Order date cannot be in the future.")
            .WithErrorCode("SalesOrder.OrderDate.NotFuture")
            .Must(d => d >= new DateOnly(2000, 1, 1))
            .WithMessage("Order date is unrealistically old.")
            .WithErrorCode("SalesOrder.OrderDate.NotTooOld");
    }
}
