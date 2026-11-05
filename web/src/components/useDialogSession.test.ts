import { useEffect } from "react";
import { act, renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { useDialogSession } from "./useDialogSession";

// #477 part 2 — the generation itself, away from any screen. SalesPage proves
// the behaviour a user sees; these pin the rules a screen is entitled to rely
// on, so a later reader can change one without re-deriving the other.
describe("useDialogSession", () => {
  it("keeps a claim current while nothing supersedes it", () => {
    const { result } = renderHook(() => useDialogSession());
    act(() => result.current.begin("create-order"));
    const claimed = result.current.claim("create-order");

    expect(result.current.isCurrent("create-order", claimed)).toBe(true);
  });

  it("supersedes a claim once the dialog begins again", () => {
    const { result } = renderHook(() => useDialogSession());
    act(() => result.current.begin("create-order"));
    const claimed = result.current.claim("create-order");
    act(() => result.current.begin("create-order")); // dismissed, or reopened

    expect(result.current.isCurrent("create-order", claimed)).toBe(false);
  });

  it("supersedes only the scope that began again", () => {
    // The scopes are independent: cancelling one dialog must not silence a
    // write another one has in flight. A single shared counter would pass every
    // test above and fail this one, which is the whole reason it is here.
    const { result } = renderHook(() => useDialogSession());
    act(() => {
      result.current.begin("create-order");
      result.current.begin("record-payment");
    });
    const payment = result.current.claim("record-payment");
    act(() => result.current.begin("create-order"));

    expect(result.current.isCurrent("record-payment", payment)).toBe(true);
  });

  it("treats a never-opened dialog as current", () => {
    // Absent means "no session has ever begun", and the claim of 0 matches it.
    // While it is mounted, a screen that has not adopted `begin` therefore
    // behaves as it did before this hook existed, rather than silently gating
    // every success off — which would be a far worse failure than the one the
    // hook fixes. Unmount ends it like any other session.
    const { result } = renderHook(() => useDialogSession());
    const claimed = result.current.claim("never-opened");

    expect(claimed).toBe(0);
    expect(result.current.isCurrent("never-opened", claimed)).toBe(true);
  });

  it("survives more than one supersession without a claim coming back", () => {
    // A monotonic counter, not a boolean: three cancel/reopen cycles must not
    // wrap around to the value an early attempt is still holding.
    const { result } = renderHook(() => useDialogSession());
    act(() => result.current.begin("create-order"));
    const first = result.current.claim("create-order");
    act(() => {
      result.current.begin("create-order");
      result.current.begin("create-order");
      result.current.begin("create-order");
    });

    expect(result.current.isCurrent("create-order", first)).toBe(false);
  });

  it("keeps a claim current through StrictMode's dev remount", () => {
    // StrictMode runs the mount effect, its cleanup, then the effect again; the
    // second run must undo the first cleanup or no dev write is ever sent. Two
    // renders alone do not prove the replay, so the probe counts the effect.
    let setups = 0;
    let cleanups = 0;
    const { result } = renderHook(() => {
      useEffect(() => {
        setups += 1;
        return () => { cleanups += 1; };
      }, []);
      return useDialogSession();
    }, { reactStrictMode: true });
    expect({ setups, cleanups }).toEqual({ setups: 2, cleanups: 1 }); // the mount effect was replayed
    act(() => result.current.begin("create-order"));
    const claimed = result.current.claim("create-order");

    expect(result.current.isCurrent("create-order", claimed)).toBe(true);
  });

  it("supersedes every claim once the screen unmounts", () => {
    // #1280 review — an Owner who leaves the screen while a step-up grant is
    // being issued has left the dialog too; the write must not follow.
    const { result, unmount } = renderHook(() => useDialogSession());
    act(() => result.current.begin("create-order"));
    const opened = result.current.claim("create-order");
    const neverOpened = result.current.claim("never-opened");
    unmount();

    expect(result.current.isCurrent("create-order", opened)).toBe(false);
    expect(result.current.isCurrent("never-opened", neverOpened)).toBe(false);
  });
});
