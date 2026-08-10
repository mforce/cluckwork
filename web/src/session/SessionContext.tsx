import { createContext, useCallback, useContext, useEffect, useState } from "react";
import type { ReactNode } from "react";
import { getMe, getAccount } from "../api/cluckwork";
import type { Account, Me } from "../api/cluckwork";
import { BrandSplash } from "../components/BrandSplash";
import { FarmProvider } from "../farm/FarmContext";
import i18n from "../i18n";
import { resolveLanguage } from "../i18n/resolve";

// #179 — sessionStorage, not localStorage: "once per login" means once per
// browser tab's current session, not once ever. A fresh tab (new sessionStorage)
// shows it again even with a live token, which is the accepted definition of
// "per login" from the design discussion — sessionStorage is per-tab by
// construction, so this needs no extra bookkeeping to get that for free.
const SPLASH_SHOWN_KEY = "cluckwork.splashShown";

// Codex review of #496: the marker above is otherwise never cleared, so once
// dismissed in a tab it stays suppressed across logout/login in that tab —
// including a different user signing in. AuthContext.login calls this before
// marking the app authenticated, so "once per login" is actually per login,
// not per tab lifetime; a silent token refresh (same session, not a new
// login) deliberately does NOT call this.
// eslint-disable-next-line react-refresh/only-export-components
export function clearSplashSeenMarker(): void {
  sessionStorage.removeItem(SPLASH_SHOWN_KEY);
}

// eslint-disable-next-line react-refresh/only-export-components
export const MeContext = createContext<Me | null>(null);
// eslint-disable-next-line react-refresh/only-export-components
export function useMe(): Me | null {
  return useContext(MeContext);
}

// #444 — patch fields of the bootstrapped Me in place. The language selector
// never needed this (it switches i18next directly and reconciles from /me on
// the next bootstrap), but the stepper-unit preference is READ live through
// useMe() by DailyEntryPage — without a way to update the context, a change
// on the Account screen would not apply until the next login. A no-op default
// so the selector stays renderable (and testable) outside SessionProvider.
// eslint-disable-next-line react-refresh/only-export-components
export const MeUpdateContext = createContext<(patch: Partial<Me>) => void>(() => {});
// eslint-disable-next-line react-refresh/only-export-components
export function useMeUpdate(): (patch: Partial<Me>) => void {
  return useContext(MeUpdateContext);
}

// The coordinated authenticated bootstrap (#182). Sits inside ProtectedRoute
// (so it has a token) and BELOW it in the tree, wrapping the shell. It is the
// ONLY place the UI language is set after login, and it GATES the shell until:
// /me + /account are read concurrently, the language is resolved, and i18next is
// switched to it. That closes the one-frame-English window AuthContext opens by
// marking the user authenticated the instant a token arrives. It hands the
// already-read account to FarmProvider so /account is read exactly once.
export function SessionProvider({ children }: { children: ReactNode }) {
  // One settled result. me/account are independent: /me failing must NOT discard
  // a good /account (its timezone/locale drive date correctness), and vice versa.
  const [result, setResult] = useState<{ me: Me | null; account: Account | null } | null>(null);

  // #179 — read once, at construction, not derived from `result` on every
  // render: sessionStorage is the durable "have I shown it THIS tab" record,
  // but the boolean that drives rendering has to be React state so dismissing
  // it re-renders.
  const [splashDismissed, setSplashDismissed] = useState(
    () => sessionStorage.getItem(SPLASH_SHOWN_KEY) === "1");

  // #444 — see MeUpdateContext. A patch against a failed /me read (me === null)
  // is dropped: there is no profile to patch, and fabricating one from a
  // Partial would put an object with made-up identity fields into MeContext.
  const patchMe = useCallback((patch: Partial<Me>) => {
    setResult((prev) =>
      prev === null || prev.me === null ? prev : { ...prev, me: { ...prev.me, ...patch } });
  }, []);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      // allSettled, not all: preserve whichever read succeeded. account === null
      // means the ACCOUNT read itself failed → FarmProvider shows its own banner.
      const [meR, accR] = await Promise.allSettled([getMe(), getAccount()]);
      if (cancelled) return;
      const me = meR.status === "fulfilled" ? meR.value : null;
      const account = accR.status === "fulfilled" ? accR.value : null;
      // Resolve from whatever we have; missing signals fall through resolveLanguage
      // to the farm subtag then English. Guarded so a throwing changeLanguage can
      // never wedge the gate — we still reveal the shell on the current language.
      try {
        await i18n.changeLanguage(resolveLanguage(me?.language, account?.locale));
      } catch (err) {
        // keep the current (English) language; still reveal the shell below
        console.warn("Failed to switch UI language during bootstrap", err);
      }
      if (cancelled) return;
      setResult({ me, account });
    })();
    return () => { cancelled = true; };
  }, []);

  if (!result) return null; // gate: shell hidden until reads settle + language set

  // Skipped entirely when the farm has no banner (never an empty splash) or
  // the account read itself failed (nothing to show); shown at most once per
  // tab session regardless of how many times SessionProvider itself remounts.
  const bannerContentHash = result.account?.bannerContentHash ?? null;
  const showSplash = !splashDismissed && bannerContentHash !== null;

  function dismissSplash() {
    sessionStorage.setItem(SPLASH_SHOWN_KEY, "1");
    setSplashDismissed(true);
  }

  // FarmProvider mounts ONLY here, once, with a stable initialAccount (SessionProvider
  // returned null until this point and never changes result), so no stale-seed sync
  // effect is needed.
  return (
    <MeContext.Provider value={result.me}>
      <MeUpdateContext.Provider value={patchMe}>
        <FarmProvider initialAccount={result.account}>
          {showSplash && (
            <BrandSplash
              farmName={result.account?.name ?? ""}
              bannerContentHash={bannerContentHash!}
              onDismiss={dismissSplash}
            />
          )}
          {/* display: contents — this wrapper's only job is to carry `inert`
              while the splash is open; it must not become a box of its own,
              or AppLayout's shell (which assumes it sits directly in flow)
              could size wrong. inert applies to descendants regardless of the
              element's own display value. */}
          <div inert={showSplash || undefined} style={{ display: "contents" }}>
            {children}
          </div>
        </FarmProvider>
      </MeUpdateContext.Provider>
    </MeContext.Provider>
  );
}
