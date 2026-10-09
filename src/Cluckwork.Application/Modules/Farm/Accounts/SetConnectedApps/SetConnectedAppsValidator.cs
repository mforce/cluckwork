using Cluckwork.Application.Modules.Farm.Contracts;
using FluentValidation;

namespace Cluckwork.Application.Modules.Farm.Accounts.SetConnectedApps;

public sealed class SetConnectedAppsValidator : AbstractValidator<SetConnectedAppsCommand>
{
    public SetConnectedAppsValidator()
    {
        RuleFor(x => x.Allow)
            .NotNull()
            .WithErrorCode("ConnectedApps.Allow.Required");

        RuleFor(x => x.Version)
            .GreaterThanOrEqualTo(0)
            .WithErrorCode("ConnectedApps.Version.NonNegative");
    }
}
