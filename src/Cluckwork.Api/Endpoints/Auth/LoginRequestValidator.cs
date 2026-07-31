namespace Cluckwork.Api.Endpoints.Auth;

using Cluckwork.Application.Features.Users;
using FluentValidation;

// #309 — MAX-length-only guard on the login credentials, plus a not-null guard
// (see below). Deliberately NO NotEmpty / MinLength: an empty ("") or short
// email/password must still flow into identity.LoginAsync and come back as the
// generic, non-enumerating 401 — the validator must not change that behaviour
// or reveal anything about a field's content. Only an OVERSIZED field
// short-circuits to a 400, bounding the input before it reaches the PBKDF2
// verify (including the unknown-user timing-equalization hash). Lives in the
// Api assembly because it validates the Api LoginRequest DTO.
public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        // A JSON body with an explicit `null` for either field binds LoginRequest
        // with a null Email/Password (MaximumLength tolerates null, so it alone
        // let this through). That null then reached UserManager.FindByEmailAsync
        // / CheckPasswordAsync / VerifyHashedPassword, all of which throw
        // ArgumentNullException — uncaught by /error's exception switch, so it
        // fell through to an unhandled 500. The `Must(v => v is not null)` guard
        // rejects null with a clean 400 while still passing "" through unchanged
        // (the predicate is only false for null) so the empty-string path above
        // is untouched. Cascade(Stop) is scoped to each RuleFor chain — it stops
        // evaluating MaximumLength once null is already flagged (avoiding a
        // confusing double-error) and doesn't affect FluentValidation's default
        // Continue mode used elsewhere in this codebase.
        RuleFor(x => x.Email)
            .Cascade(CascadeMode.Stop)
            .Must(v => v is not null).WithMessage("Email is required.").WithErrorCode("Auth.Email.Required")
            .MaximumLength(256).WithErrorCode("Auth.Email.MaxLength");
        RuleFor(x => x.Password)
            .Cascade(CascadeMode.Stop)
            .Must(v => v is not null).WithMessage("Password is required.").WithErrorCode("Auth.Password.Required")
            .MaximumLength(PasswordRules.MaxLength).WithErrorCode("Auth.Password.MaxLength");
    }
}
