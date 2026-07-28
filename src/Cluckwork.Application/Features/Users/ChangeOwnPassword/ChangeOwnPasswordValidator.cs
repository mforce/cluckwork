namespace Cluckwork.Application.Features.Users.ChangeOwnPassword;

using FluentValidation;

public sealed class ChangeOwnPasswordValidator : AbstractValidator<ChangeOwnPasswordCommand>
{
    public ChangeOwnPasswordValidator()
    {
        RuleFor(x => x.CurrentPassword)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Your current password is required.")
            .WithErrorCode("Me.CurrentPassword.Required");
        RuleFor(x => x.NewPassword)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("A new password is required.")
            .WithErrorCode("Me.NewPassword.Required")
            .MinimumLength(Cluckwork.Application.Features.Users.PasswordRules.MinLength)
            .WithErrorCode("Me.NewPassword.MinLength");
        // Re-setting the same password would revoke every other session for no
        // gain, so refuse it outright rather than silently churning the family.
        RuleFor(x => x.NewPassword)
            .NotEqual(x => x.CurrentPassword)
            .WithMessage("The new password must be different from the current one.")
            .WithErrorCode("Me.NewPassword.Different");
    }
}
