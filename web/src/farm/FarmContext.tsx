import { createContext, useCallback, useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { getAccount } from "../api/cluckwork";
import type { Account } from "../api/cluckwork";
import { todayIso } from "../lib/dates";
import { applyBrand } from "../lib/brand";

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
  //
  // Resolves TRUE when the read succeeded. It cannot throw — the provider must
  // survive a failed read — so a caller that needs to know has to be told, and
  // the previous version left the settings screen reporting a save as fully
  // applied when the shell had in fact kept the old timezone (codex round 2).
  refresh: () => Promise<boolean>;
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
  refresh: async () => false,
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
      const account = await getAccount();
      setFarm(account);
      // The API is the source of truth for the farm's palette (#149); this also
      // refreshes the localStorage cache the pre-paint script reads next load.
      // Only on success: a failed read leaves whatever was pre-painted, since
      // clearing would turn a network blip into a colour change on a farm that
      // never changed palette.
      applyBrand(account.brand);
      setLoadFailed(false);
      // Marked settled on BOTH paths rather than in a `finally`: nothing can
      // escape the catch, so the finally's exceptional path was a branch no
      // test could ever reach — and this file is held at 100%.
      setLoaded(true);
      return true;
    } catch {
      // The farm we already had is KEPT: a failed refresh after a save leaves a
      // stale name on screen, but clearing would drop the farm's timezone and
      // silently move every date input back to browser-local. The first load
      // has nothing to keep, so it stays null — and the flag makes the shell
      // say so rather than quietly using the device's day.
      setLoadFailed(true);
      setLoaded(true);
      return false;
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const timeZoneId = farm?.timeZoneId;

  // DERIVED in render, never seeded into state. Held in state it was wrong for
  // exactly one commit — the initialiser runs while `farm` is still null, and
  // the commit that flips `loaded` is the same commit the children MOUNT in, so
  // every date field seeded its initial value from the device's day and froze
  // it there. That is the bug the gate below exists to prevent, reintroduced by
  // the fix for the midnight rollover (round 2: all four reviewers).
  const today = todayIso(timeZoneId);

  // What the rollover needs is not a stored value but a reason to re-render.
  // The poll stores the day it last saw; when the farm crosses midnight the
  // string changes, the context value changes with it, and consumers recompute.
  // While the day is unchanged React bails out of the update and nothing
  // re-renders at all.
  const [, setSeenDay] = useState(today);
  useEffect(() => {
    const timer = setInterval(() => setSeenDay(todayIso(timeZoneId)), DAY_POLL_MS);
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
