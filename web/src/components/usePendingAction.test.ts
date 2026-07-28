import { describe, it, expect, vi } from "vitest";
import { renderHook, act } from "@testing-library/react";
import { usePendingAction } from "./usePendingAction";

// The re-entry guard is the whole point of this hook: React state cannot stop
// two run() calls in the same tick, so every test that matters here holds the
// action's promise open (deferred, same idiom as client.test.ts) and asserts
// what happens BEFORE it settles — no timing guesses.
function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

describe("usePendingAction", () => {
  it("tracks the busy lifecycle: idle → busy while in flight → idle after settle", async () => {
    const { result } = renderHook(() => usePendingAction());
    expect(result.current.busy).toBe(false);

    const gate = deferred<string>();
    let flight!: Promise<string | undefined>;
    act(() => {
      flight = result.current.run("save", () => gate.promise);
    });
    expect(result.current.busy).toBe(true);

    await act(async () => {
      gate.resolve("saved");
      await flight;
    });
    expect(result.current.busy).toBe(false);
    await expect(flight).resolves.toBe("saved");
  });

  it("runs the action exactly once for two run() calls in the same tick", async () => {
    const { result } = renderHook(() => usePendingAction());
    const gate = deferred<void>();
    const action = vi.fn(() => gate.promise);

    let first!: Promise<void | undefined>;
    let second!: Promise<void | undefined>;
    act(() => {
      // No await between these two — this is the double-click that state-based
      // guards let through.
      first = result.current.run("save", action);
      second = result.current.run("save", action);
    });
    expect(action).toHaveBeenCalledTimes(1);
    await expect(second).resolves.toBeUndefined();

    await act(async () => {
      gate.resolve();
      await first;
    });
    expect(action).toHaveBeenCalledTimes(1);
    expect(result.current.busy).toBe(false);
  });

  it("returns undefined on a skipped run, and the winner's value on the real one", async () => {
    const { result } = renderHook(() => usePendingAction());
    const gate = deferred<number>();

    let winner!: Promise<number | undefined>;
    let skipped!: Promise<number | undefined>;
    act(() => {
      winner = result.current.run("save", () => gate.promise);
      skipped = result.current.run("save", () => gate.promise);
    });

    await expect(skipped).resolves.toBeUndefined();
    await act(async () => {
      gate.resolve(7);
      await winner;
    });
    await expect(winner).resolves.toBe(7);
  });

  it("propagates a rejection, closes the flight, and lets the next run proceed", async () => {
    const { result } = renderHook(() => usePendingAction());
    const gate = deferred<never>();

    let flight!: Promise<undefined>;
    act(() => {
      flight = result.current.run("save", () => gate.promise);
    });
    expect(result.current.busy).toBe(true);

    await act(async () => {
      gate.reject(new Error("boom"));
      await expect(flight).rejects.toThrow("boom");
    });
    expect(result.current.busy).toBe(false);

    // The failed flight must not wedge the guard shut.
    const next = vi.fn(async () => "again");
    let retried: string | undefined;
    await act(async () => {
      retried = await result.current.run("save", next);
    });
    expect(next).toHaveBeenCalledTimes(1);
    expect(retried).toBe("again");
  });

  it("isPending(scope) matches only the active scope, and clears after settle", async () => {
    const { result } = renderHook(() => usePendingAction());
    const gate = deferred<void>();

    let flight!: Promise<void | undefined>;
    act(() => {
      flight = result.current.run("archive:7", () => gate.promise);
    });
    expect(result.current.isPending("archive:7")).toBe(true);
    // Sibling row and sibling verb stay quiet — this is what keeps exactly one
    // control spinning while the rest merely disable.
    expect(result.current.isPending("archive:8")).toBe(false);
    expect(result.current.isPending("deplete:7")).toBe(false);

    await act(async () => {
      gate.resolve();
      await flight;
    });
    expect(result.current.isPending("archive:7")).toBe(false);
  });
});
