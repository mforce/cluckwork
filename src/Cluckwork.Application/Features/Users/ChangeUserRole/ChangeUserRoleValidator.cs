using Cluckwork.Application.Features.Users.CreateUser;
using FluentValidation;

namespace Cluckwork.Application.Features.Users.ChangeUserRole;

public sealed class ChangeUserRoleValidator : AbstractValidator<ChangeUserRoleCommand>
{
    public ChangeUserRoleValidator()
    {
        RuleFor(x => x.Role)
            .Must(r => r == CreateUserValidator.WorkerRole || Cluckwork.Domain.Accounts.Roles.Assignable.Contains(r))
            .WithMessage("Role must be Admin (owner), Manager, Sales, ReadOnly, or Worker.")
            .WithErrorCode("User.Role.Allowed");
    }
}
