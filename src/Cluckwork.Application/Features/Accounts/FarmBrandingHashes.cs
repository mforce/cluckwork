namespace Cluckwork.Application.Features.Accounts;

[ModuleContract("Farm")]
public sealed record FarmBrandingHashes(string? LogoContentHash, string? BannerContentHash);
