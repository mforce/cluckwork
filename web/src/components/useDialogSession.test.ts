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
    // A screen that has not adopted `begin` therefore behaves exactly as it did
    // before this hook existed, rather than silently gating every success off —
    // which would be a far worse failure than the one the hook fixes.
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
});
