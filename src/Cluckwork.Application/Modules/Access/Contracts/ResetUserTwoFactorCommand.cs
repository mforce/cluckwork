namespace Cluckwork.Application.Modules.Access.Contracts;

// #1280 — no body: the route's id and the step-up header.
public sealed record ResetUserTwoFactorCommand(Guid UserId, string? StepUpToken);
