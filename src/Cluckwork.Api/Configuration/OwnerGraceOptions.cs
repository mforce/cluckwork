using Microsoft.Extensions.Options;

namespace Cluckwork.Api.Configuration;

// #1281 (#1274 decision 18) — how many days an Owner may go without two-factor,
// counted from SecondFactorRequiredSince. A whole number from 1 to 365, checked
// when a serving process boots, so no value ever means "enforce now". Raising
// it and restarting moves every Owner's deadline out at once. The only setting
// the Owner two-factor gate reads.
public sealed class OwnerGraceOptions
{
    public const string SectionName = "TwoFactor";
    public const int DefaultDays = 14;
    public const int MinDays = 1;
    public const int MaxDays = 365;

    public int OwnerGraceDays { get; init; } = DefaultDays;

    // A raised grace logs one warning at startup, so a forgotten extension stays visible.
    public static void WarnIfExtended(IServiceProvider services, ILogger logger)
    {
        var days = services.GetRequiredService<IOptions<OwnerGraceOptions>>().Value.OwnerGraceDays;
        if (days > DefaultDays)
            logger.LogWarning(
                "Owner two-factor grace extended: TwoFactor:OwnerGraceDays is {OwnerGraceDays}, above the default {DefaultOwnerGraceDays}",
                days, DefaultDays);
    }
}
