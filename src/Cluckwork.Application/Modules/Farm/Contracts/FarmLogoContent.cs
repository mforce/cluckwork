namespace Cluckwork.Application.Modules.Farm.Contracts;

[ModuleContract("Farm")]
public sealed record FarmLogoContent(
    byte[] Content, string ContentType, string ContentHash, DateTimeOffset UpdatedAt);
