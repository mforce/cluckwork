import { createContext, useCallback, useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { getAccount } from "../api/cluckwork";
import type { Account } from "../api/cluckwork";

export interface FarmState {
  // Null until /account answers, and after a first load that failed.
  farm: Account | null;
  // Re-read /account. The settings screen calls this after a save so the
  // branding slot and every date input pick the change up without a reload
  // (#123, §4.5 "settings changes reflect immediately").
  refresh: () => Promise<void>;
}

// Defaulted rather than null-and-throw, unlike AuthContext. "No farm loaded"
// is a state the app is genuinely in — during the first fetch, and after one
// that failed — and every consumer already has to handle it: the branding slot
// falls back to app branding, and todayIso() falls back to browser-local. A
// hook that threw would turn a slow network into a blank screen, and would
// force the provider into every screen's test render for a value those tests
// do not exercise.
export const FarmContext = createContext<FarmState>({
  farm: null,
  refresh: async () => {},
});

// One /account read for the whole authenticated shell (#123). Before this,
// screens that needed the currency each fetched it themselves; the farm's
// timezone is now needed by every screen with a date field, which makes a
// shared read the only sensible shape.
export function FarmProvider({ children }: { children: ReactNode }) {
  const [farm, setFarm] = useState<Account | null>(null);
  const [loaded, setLoaded] = useState(false);

  const refresh = useCallback(async () => {
    try {
      setFarm(await getAccount());
    } catch {
      // Keep whatever we already had: a failed refresh after a save is a
      // stale screen, but clearing would drop the farm's timezone and silently
      // move every date input back to browser-local. The first load has
      // nothing to keep, so it stays null and the fallbacks apply.
    } finally {
      setLoaded(true);
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const value = useMemo(() => ({ farm, refresh }), [farm, refresh]);

  // Hold the shell until the first read settles, the way ProtectedRoute holds
  // it for the session bootstrap. Date inputs seed their initial value at
  // mount, so a screen that mounts before the timezone is known would prefill
  // the browser's today and never correct itself.
  if (!loaded) return null;

  return <FarmContext.Provider value={value}>{children}</FarmContext.Provider>;
}
