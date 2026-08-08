import { createContext, useCallback, useContext, useEffect, useState } from "react";
import type { ReactNode } from "react";
import { getMe, getAccount } from "../api/cluckwork";
import type { Account, Me } from "../api/cluckwork";
import { FarmProvider } from "../farm/FarmContext";
import i18n from "../i18n";
import { resolveLanguage } from "../i18n/resolve";

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

  // FarmProvider mounts ONLY here, once, with a stable initialAccount (SessionProvider
  // returned null until this point and never changes result), so no stale-seed sync
  // effect is needed.
  return (
    <MeContext.Provider value={result.me}>
      <MeUpdateContext.Provider value={patchMe}>
        <FarmProvider initialAccount={result.account}>{children}</FarmProvider>
      </MeUpdateContext.Provider>
    </MeContext.Provider>
  );
}
