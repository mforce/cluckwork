namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.Configuration;
using Cluckwork.Domain.Media;
using Microsoft.Extensions.Options;

// #123 — the operational upload cap is config, validated at boot so a
// misconfigured value fails the start rather than the first upload. A value
// above the ceiling would hand every request a buffer larger than the column
// can store, which is the whole reason the ceiling is a fixed invariant.
public sealed class FarmLogoOptionsTests
{
    private static ValidateOptionsResult Validate(int maxUploadBytes) =>
        new FarmLogoOptionsValidator().Validate(
            name: null, new FarmLogoOptions { MaxUploadBytes = maxUploadBytes });

    [Fact]
    public void The_default_is_valid()
    {
        Assert.True(new FarmLogoOptionsValidator()
            .Validate(name: null, new FarmLogoOptions()).Succeeded);
    }

    [Fact]
    public void A_value_at_the_ceiling_is_accepted()
    {
        Assert.True(Validate(ImageSanitizer.MaxByteLengthCeiling).Succeeded);
    }

    [Fact]
    public void A_value_one_byte_over_the_ceiling_is_rejected()
    {
        var result = Validate(ImageSanitizer.MaxByteLengthCeiling + 1);
        Assert.True(result.Failed);
        Assert.Contains("ceiling", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_value_is_rejected(int value)
    {
        Assert.True(Validate(value).Failed);
    }
}
