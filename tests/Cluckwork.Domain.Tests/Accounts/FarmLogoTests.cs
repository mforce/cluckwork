namespace Cluckwork.Domain.Tests.Accounts;

using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Media;

// #179 — the farm banner shares FarmLogo's row (see FarmLogo.cs for why: two
// independent branding images, one shared Version token, accepted as a rare
// false-conflict rather than a second table). These tests cover what's new —
// the banner side and its independence from the logo side. Replace()'s own
// field-mapping is already covered end-to-end by
// Cluckwork.Api.IntegrationTests/FarmLogoTests.cs.
public sealed class FarmLogoTests
{
    private static SanitizedImage Image(int seed = 1) =>
        new(ImageKind.Png, [(byte)seed, (byte)(seed + 1)], Width: 10 + seed, Height: 20 + seed);

    private static FarmLogo Bare() =>
        FarmLogo.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Create_StartsWithNeitherLogoNorBanner()
    {
        var logo = Bare();

        Assert.False(logo.HasLogo);
        Assert.False(logo.HasBanner);
        Assert.Equal(0, logo.Version);
    }

    [Fact]
    public void Replace_SetsLogoFields_AndBumpsVersion_WithoutTouchingBanner()
    {
        var logo = Bare();
        var now = DateTimeOffset.UtcNow;

        logo.Replace(Image(1), now);

        Assert.True(logo.HasLogo);
        Assert.False(logo.HasBanner);
        Assert.Equal(1, logo.Version);
        Assert.Equal("image/png", logo.ContentType);
        Assert.Equal(11, logo.Width);
        Assert.Equal(21, logo.Height);
        Assert.Null(logo.BannerContentHash);
    }

    [Fact]
    public void ReplaceBanner_SetsBannerFields_AndBumpsVersion_WithoutTouchingLogo()
    {
        var logo = Bare();
        var now = DateTimeOffset.UtcNow;

        logo.ReplaceBanner(Image(1), now);

        Assert.True(logo.HasBanner);
        Assert.False(logo.HasLogo);
        Assert.Equal(1, logo.Version);
        Assert.Equal("image/png", logo.BannerContentType);
        Assert.Equal(11, logo.BannerWidth);
        Assert.Equal(21, logo.BannerHeight);
        Assert.Null(logo.ContentHash);
    }

    [Fact]
    public void LogoAndBanner_AreIndependent_AndShareOneVersionCounter()
    {
        var logo = Bare();
        var now = DateTimeOffset.UtcNow;

        logo.Replace(Image(1), now);
        logo.ReplaceBanner(Image(2), now);

        Assert.True(logo.HasLogo);
        Assert.True(logo.HasBanner);
        // Accepted tradeoff (#179): one shared token, so an unrelated banner
        // write also advances the logo's concurrency version.
        Assert.Equal(2, logo.Version);
        Assert.NotEqual(logo.ContentHash, logo.BannerContentHash);
    }

    [Fact]
    public void ClearLogo_NullsOnlyLogoFields_AndBumpsVersion()
    {
        var logo = Bare();
        var now = DateTimeOffset.UtcNow;
        logo.Replace(Image(1), now);
        logo.ReplaceBanner(Image(2), now);

        logo.ClearLogo();

        Assert.False(logo.HasLogo);
        Assert.Null(logo.ContentHash);
        Assert.True(logo.HasBanner);
        Assert.NotNull(logo.BannerContentHash);
        Assert.Equal(3, logo.Version);
    }

    [Fact]
    public void ClearBanner_NullsOnlyBannerFields_AndBumpsVersion()
    {
        var logo = Bare();
        var now = DateTimeOffset.UtcNow;
        logo.Replace(Image(1), now);
        logo.ReplaceBanner(Image(2), now);

        logo.ClearBanner();

        Assert.False(logo.HasBanner);
        Assert.Null(logo.BannerContentHash);
        Assert.True(logo.HasLogo);
        Assert.NotNull(logo.ContentHash);
        Assert.Equal(3, logo.Version);
    }
}
