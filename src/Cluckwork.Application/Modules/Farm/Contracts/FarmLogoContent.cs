namespace Cluckwork.Application.Modules.Farm.Contracts;

public sealed record FarmLogoContent(
    byte[] Content, string ContentType, string ContentHash, DateTimeOffset UpdatedAt);
