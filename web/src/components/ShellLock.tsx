import { createContext, useContext, useEffect } from "react";

// #1277 MC3 — a screen holds the app shell (the rail and the bottom nav) inert
// while a write the user must not walk away from is in flight. AppLayout
// counts the holders, so two screens' holds never release each other's.
export const ShellLockContext = createContext<(delta: 1 | -1) => void>(() => {});

export function useShellLock(locked: boolean) {
  const adjust = useContext(ShellLockContext);
  useEffect(() => {
    if (!locked) return;
    adjust(1);
    return () => adjust(-1);
  }, [locked, adjust]);
}
