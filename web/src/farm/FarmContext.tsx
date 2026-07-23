import { createContext, useCallback, useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { getAccount } from "../api/cluckwork";
import type { Account } from "../api/cluckwork";
import { todayIso } from "../lib/dates";

export interface FarmState {
  // Null until /account answers, and after a read that failed.
  farm: Account | null;
  // True when the most recent read failed. With `farm` still null it means the
  // shell never got one, and the shell says so: without a banner, dates
  // silently follow the DEVICE's day and the screen looks perfectly healthy
  // while being a day out (codex review of #123). It is also what tells a
  // consumer apart from one rendered with no provider at all, where the same
  // null farm is simply "not applicable".
  loadFailed: boolean;
  // Farm-local today, kept current as the farm's day rolls over. Null OUTSIDE a
  // provider, where useFarmToday() computes browser-local live instead.
  today: string | null;
  // Re-read /account. The settings screen calls this after a save so the
  // branding slot and every date input pick the change up without a reload
  // (#123, §4.5 "settings changes reflect immediately"). Also the banner's
  // retry.
  refresh: () => Promise<void>;
}

// How often the farm's day is re-checked. A minute is far finer than the thing
// being watched (a date rolling over) and costs one string comparison — the
// state update is skipped when the day has not changed, so no consumer
// re-renders in between.
const DAY_POLL_MS = 60_000;

// Defaulted rather than null-and-throw, unlike AuthContext. "No farm loaded"
// is a state the app is genuinely in — during the first fetch, and after one
// that failed — and every consumer already handles it: the branding slot falls
// back to app branding, and todayIso() falls back to browser-local. A hook that
// threw would turn a slow network into a blank screen, and would force the
// provider into every screen's test render for a value those tests do not
// exercise.
export const FarmContext = createContext<FarmState>({
  farm: null,
  loadFailed: false,
  today: null,
  refresh: async () => {},
});

// One /account read for the whole authenticated shell (#123). Before this,
// screens that needed the currency each fetched it themselves; the farm's
// timezone is now needed by every screen with a date field, which makes a
// shared read the only sensible shape.
export function FarmProvider({ children }: { children: ReactNode }) {
  const [farm, setFarm] = useState<Account | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [loadFailed, setLoadFailed] = useState(false);

  const refresh = useCallback(async () => {
    try {
      setFarm(await getAccount());
      setLoadFailed(false);
    } catch {
      // The farm we already had is KEPT: a failed refresh after a save leaves a
      // stale name on screen, but clearing would drop the farm's timezone and
      // silently move every date input back to browser-local. The first load
      // has nothing to keep, so it stays null — and the flag makes the shell
      // say so rather than quietly using the device's day.
      setLoadFailed(true);
    } finally {
      setLoaded(true);
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const timeZoneId = farm?.timeZoneId;
  const [today, setToday] = useState(() => todayIso(timeZoneId));

  // The farm's day is read once per render everywhere it is used, but nothing
  // re-renders on the clock alone — so a tab left open across farm-midnight
  // would keep offering yesterday as the latest selectable date (codex review
  // of #123). This is the thing that moves.
  useEffect(() => {
    setToday(todayIso(timeZoneId));
    const timer = setInterval(
      // Same string → React bails out of the update, so the common case costs
      // nothing beyond the comparison.
      () => setToday(todayIso(timeZoneId)),
      DAY_POLL_MS);
    return () => clearInterval(timer);
  }, [timeZoneId]);

  const value = useMemo(
    () => ({ farm, loadFailed, today, refresh }),
    [farm, loadFailed, today, refresh]);

  // Hold the shell until the first read settles, the way ProtectedRoute holds
  // it for the session bootstrap. Date inputs seed their initial value at
  // mount, so a screen that mounts before the timezone is known would prefill
  // the browser's today and never correct itself.
  if (!loaded) return null;

  return <FarmContext.Provider value={value}>{children}</FarmContext.Provider>;
}
