import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import type { FeedUsage, WaterUsage } from "../../api/cluckwork";
import { useFormat } from "../../farm/useFormat";

interface DaySupportLineProps {
  daySupport: { feed: FeedUsage[]; water: WaterUsage[] } | null;
  flockId: string;
  date: string;
}

// The #446 day-support strip: what else this flock's day already carries.
export function DaySupportLine({ daySupport, flockId, date }: DaySupportLineProps) {
  const { t } = useTranslation(["dailyEntry", "dashboard"]);
  const fmt = useFormat();

  return (
    <>
      {/* #446 — what else this flock's day already carries, with the way to
          the pages that record it. Joined on flock+date (never DailyEntryId),
          so it's live before the day's entry exists. */}
      {daySupport && (() => {
        // One currency per farm is the norm, but lot costs snapshot their
        // purchase-time currency — summing across a historical currency
        // change would blend units, so the cost drops rather than lies
        // (quality review of #446).
        const oneCurrency = daySupport.feed.every(
          (r) => r.currencyCode === daySupport.feed[0].currencyCode);
        const dayParams = `flockId=${flockId}&from=${date}&to=${date}`;
        return (
          <p className="hint">
            <Link className="link" to={`/feed?${dayParams}`}>
              {daySupport.feed.length === 0
                ? t("daySupportFeedNone")
                : oneCurrency
                  ? t("daySupportFeed", {
                      count: daySupport.feed.length,
                      cost: fmt.money(
                        daySupport.feed.reduce((a, r) => a + r.estimatedCostMinorUnits, 0),
                        daySupport.feed[0].currencyCode, daySupport.feed[0].currencyMinorUnit),
                    })
                  : t("daySupportFeedNoCost", { count: daySupport.feed.length })}
            </Link>
            {" · "}
            <Link className="link" to={`/water?${dayParams}`}>
              {daySupport.water.length > 0
                ? t("daySupportWater", { count: daySupport.water.length })
                : t("daySupportWaterNone")}
            </Link>
          </p>
        );
      })()}
    </>
  );
}
