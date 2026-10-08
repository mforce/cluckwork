namespace Cluckwork.Application.Modules.Access.Contracts;

public sealed record OAuthPurgeResult(long Tokens, long Authorizations, int Applications);
