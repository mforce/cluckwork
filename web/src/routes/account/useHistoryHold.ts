import { useEffect } from "react";

const SENTINEL = "cluckworkHold";
const onSentinel = () => (window.history.state as Record<string, unknown> | null)?.[SENTINEL] === true;

// #1277 MC3 — while held, Back stays on the page: a sentinel entry sits on top
// of this one, and a Back that pops it pushes it again. It copies the router's
// own state, so the router sees the same location and nothing remounts.
// Closing the tab asks the browser to confirm. Letting go pops the sentinel,
// so history is left as it was.
export function useHistoryHold(held: boolean) {
  useEffect(() => {
    if (!held) return;
    const push = () => window.history.pushState({ ...(window.history.state as object | null), [SENTINEL]: true }, "");
    const onPop = () => { if (!onSentinel()) push(); };
    const onUnload = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      e.returnValue = "";
    };
    push();
    window.addEventListener("popstate", onPop);
    window.addEventListener("beforeunload", onUnload);
    return () => {
      window.removeEventListener("popstate", onPop);
      window.removeEventListener("beforeunload", onUnload);
      if (onSentinel()) window.history.back();
    };
  }, [held]);
}
