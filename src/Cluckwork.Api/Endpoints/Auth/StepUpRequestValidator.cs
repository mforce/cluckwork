namespace Cluckwork.Api.Endpoints.Auth;

using Cluckwork.Application.Features.Users;
using FluentValidation;

// #308 — MAX-length-only, matching LoginRequestValidator's rationale: an
// empty/short password must still flow to IStepUpGrantService.IssueAsync and
// come back as the same non-enumerating "Current password is incorrect."
// Only an OVERSIZED password short-circuits to a 400 here, bounding the input
// ahead of the PBKDF2 verify.
public sealed class StepUpRequestValidator : AbstractValidator<StepUpRequest>
{
    public StepUpRequestValidator()
    {
        RuleFor(x => x.Password)
            .Cascade(CascadeMode.Stop)
            .Must(v => v is not null).WithMessage("Password is required.").WithErrorCode("Auth.Password.Required")
            .MaximumLength(PasswordRules.MaxLength).WithErrorCode("Auth.Password.MaxLength");
    }
}
