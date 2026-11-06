namespace Cluckwork.Application.Modules.Access.Contracts;

// #1280 — no body: the route's id and the step-up header. The grant is a
// credential, so the record never prints it.
public sealed record ResetUserTwoFactorCommand(Guid UserId, string? StepUpToken)
{
    public override string ToString() => "[redacted]";
}
