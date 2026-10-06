namespace Cluckwork.Application.Modules.Farm.Contracts;

[ModuleContract("Farm")]
public sealed record FarmLogoMetadata(
    string ContentType, string ContentHash, int Width, int Height, int ByteLength, DateTimeOffset UpdatedAt);
