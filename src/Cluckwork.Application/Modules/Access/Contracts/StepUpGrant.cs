namespace Cluckwork.Application.Modules.Access.Contracts;

public sealed record StepUpGrant(string Token, DateTimeOffset ExpiresAt);
