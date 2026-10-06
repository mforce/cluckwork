namespace Cluckwork.Application.Features.Accounts;

[ModuleContract("Farm")]
public sealed record FarmLogoContent(
    byte[] Content, string ContentType, string ContentHash, DateTimeOffset UpdatedAt);
