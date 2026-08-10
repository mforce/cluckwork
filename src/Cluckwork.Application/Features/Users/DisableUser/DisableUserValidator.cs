namespace Cluckwork.Application.Features.Users.DisableUser;

using FluentValidation;

public sealed class DisableUserValidator : AbstractValidator<DisableUserCommand>
{
    // Well under AuditEvent.MaxReasonLength (500), which TRUNCATES rather than
    // rejects — a reason silently cut in half is worse than one refused at the
    // edge. Also what sets the endpoint's 2 KB body cap: System.Text.Json
    // escapes non-ASCII as \uXXXX (6 bytes per character), so 200 characters is
    // ~1.2 KB on the wire in the worst case (the same measurement #309 recorded
    // for CreateUser).
    public const int MaxReasonLength = 200;

    public DisableUserValidator()
    {
        RuleFor(x => x.Reason)
            .MaximumLength(MaxReasonLength)
            .WithMessage($"A reason may be at most {MaxReasonLength} characters.")
            .WithErrorCode("User.Reason.MaxLength");
    }
}
