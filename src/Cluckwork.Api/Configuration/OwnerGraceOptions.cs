using Microsoft.Extensions.Options;

namespace Cluckwork.Api.Configuration;

// #1281 (#1274 decision 18) — how many days an Owner may go without two-factor,
// counted from SecondFactorRequiredSince. A whole number from 1 to 365, checked
// when a serving process boots, so no value ever means "enforce now". Raising
// it and restarting moves every Owner's deadline out at once. RequireForOwners
// switches the requirement off for every Owner (the maintainer, 2026-11-08).
// The two are the only settings the Owner two-factor gate reads.
public sealed class OwnerGraceOptions
{
    public const string SectionName = "TwoFactor";
    public const int DefaultDays = 14;
    public const int MinDays = 1;
    public const int MaxDays = 365;

    public int OwnerGraceDays { get; init; } = DefaultDays;

    public bool RequireForOwners { get; init; } = true;

    // A raised grace or a switched-off requirement logs one warning at
    // startup, so a forgotten relaxation stays visible.
    public static void WarnIfRelaxed(IServiceProvider services, ILogger logger)
    {
        var options = services.GetRequiredService<IOptions<OwnerGraceOptions>>().Value;
        if (!options.RequireForOwners)
            logger.LogWarning("Owner two-factor requirement off: TwoFactor:RequireForOwners is false, so no Owner must use two-factor");
        if (options.OwnerGraceDays > DefaultDays)
            logger.LogWarning(
                "Owner two-factor grace extended: TwoFactor:OwnerGraceDays is {OwnerGraceDays}, above the default {DefaultOwnerGraceDays}",
                options.OwnerGraceDays, DefaultDays);
    }
}
