namespace Cluckwork.Application.Modules.Farm.Contracts;

[ModuleContract("Farm")]
public sealed record FarmBrandingHashes(string? LogoContentHash, string? BannerContentHash);
