using Cluckwork.Application.Modules.Access.Contracts;
using FluentValidation;

namespace Cluckwork.Application.Modules.Access.Users.ChangeOwnPassword;

public sealed class ChangeOwnPasswordValidator : AbstractValidator<ChangeOwnPasswordCommand>
{
    public ChangeOwnPasswordValidator()
    {
        RuleFor(x => x.CurrentPassword)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Your current password is required.")
            .WithErrorCode("Me.CurrentPassword.Required")
            // #309 — bound the verified credential ahead of the PBKDF2 hash.
            .MaximumLength(Cluckwork.Application.Modules.Access.Contracts.PasswordRules.MaxLength)
            .WithErrorCode("Me.CurrentPassword.MaxLength");
        RuleFor(x => x.NewPassword)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("A new password is required.")
            .WithErrorCode("Me.NewPassword.Required")
            .MinimumLength(Cluckwork.Application.Modules.Access.Contracts.PasswordRules.MinLength)
            .WithErrorCode("Me.NewPassword.MinLength")
            .MaximumLength(Cluckwork.Application.Modules.Access.Contracts.PasswordRules.MaxLength)
            .WithErrorCode("Me.NewPassword.MaxLength");
        // Re-setting the same password would revoke every other session for no
        // gain, so refuse it outright rather than silently churning the family.
        RuleFor(x => x.NewPassword)
            .NotEqual(x => x.CurrentPassword)
            .WithMessage("The new password must be different from the current one.")
            .WithErrorCode("Me.NewPassword.Different");
    }
}
