import { describe, it, expect, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { useLatestLoad } from "./useLatestLoad";

// #1185 — each test settles deferred promises in a chosen ORDER, because the
// order the network answers in is the thing under test.

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (err: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

// One deferred per input value, and the signal each run was handed.
function loader() {
  const pending = new Map<string, ReturnType<typeof deferred<string>>>();
  const signals = new Map<string, AbortSignal>();
  const load = (input: string) => (signal: AbortSignal) => {
    const d = deferred<string>();
    pending.set(input, d);
    signals.set(input, signal);
    return d.promise;
  };
  return { pending, signals, load };
}

function host(first: string) {
  const { pending, signals, load } = loader();
  const hook = renderHook(({ input }) => useLatestLoad([input], load(input)), { initialProps: { input: first } });
  return { ...hook, pending, signals };
}

describe("useLatestLoad", () => {
  it("drops a superseded response that lands after the newer one", async () => {
    const { result, rerender, pending } = host("a");
    rerender({ input: "b" });
    await act(async () => { pending.get("b")!.resolve("rows for b"); });
    await act(async () => { pending.get("a")!.resolve("rows for a"); });
    expect(result.current.data).toBe("rows for b");
    expect(result.current.loading).toBe(false);
  });

  it("drops a superseded response that lands first, and keeps loading for the newer one", async () => {
    const { result, rerender, pending } = host("a");
    rerender({ input: "b" });
    await act(async () => { pending.get("a")!.resolve("rows for a"); });
    expect(result.current.data).toBeNull();
    expect(result.current.loading).toBe(true);
  });

  it("drops a superseded failure, so it cannot clear the newer result", async () => {
    const { result, rerender, pending } = host("a");
    rerender({ input: "b" });
    await act(async () => { pending.get("b")!.resolve("rows for b"); });
    await act(async () => { pending.get("a")!.reject(new Error("a failed")); });
    expect(result.current).toMatchObject({ data: "rows for b", error: null, loading: false });
  });

  it("aborts the superseded run's signal, and the last run's on unmount", () => {
    const { rerender, unmount, signals } = host("a");
    rerender({ input: "b" });
    expect(signals.get("a")!.aborted).toBe(true);
    expect(signals.get("b")!.aborted).toBe(false);
    unmount();
    expect(signals.get("b")!.aborted).toBe(true);
  });

  it("reports the current run's failure with no data, and retry loads the same inputs again", async () => {
    const load = vi.fn<(signal: AbortSignal) => Promise<string>>()
      .mockRejectedValueOnce(new Error("offline"))
      .mockResolvedValueOnce("rows");
    const { result } = renderHook(() => useLatestLoad(["a"], load));
    await act(async () => {});
    expect(result.current).toMatchObject({ data: null, loading: false });
    expect(result.current.error).toEqual(new Error("offline"));
    await act(async () => { result.current.retry(); });
    expect(result.current).toMatchObject({ data: "rows", error: null, loading: false });
    expect(load).toHaveBeenCalledTimes(2);
  });

  it("drops the old run's response the moment retry is called, before the new run starts", async () => {
    const calls: ReturnType<typeof deferred<string>>[] = [];
    const { result } = renderHook(() => useLatestLoad(["a"], () => {
      const d = deferred<string>();
      calls.push(d);
      return d.promise;
    }));
    const save = deferred<void>();
    // A handler that saves, then retries: the old load answers in the gap
    // after retry and before React runs the replacement effect.
    await act(async () => {
      const handler = save.promise.then(() => result.current.retry());
      save.resolve();
      await handler;
      calls[0].resolve("rows from before the save");
      // Let the old run settle completely before act lets the effect run.
      await new Promise((settled) => setTimeout(settled, 0));
    });
    expect(calls).toHaveLength(2);
    expect(result.current).toMatchObject({ data: null, loading: true });
  });

  it("reports a load that throws before returning a promise as an error, not a crash", async () => {
    const { result } = renderHook(() => useLatestLoad(["a"], (): Promise<string> => {
      throw new RangeError("Invalid time value");
    }));
    await act(async () => {});
    expect(result.current).toMatchObject({ data: null, loading: false });
    expect(result.current.error).toEqual(new RangeError("Invalid time value"));
  });

  it("does not reload when only the load function's identity changes", async () => {
    const load = vi.fn(async () => "rows");
    const { rerender } = renderHook(() => useLatestLoad(["a"], () => load()));
    await act(async () => {});
    rerender();
    await act(async () => {});
    expect(load).toHaveBeenCalledTimes(1);
  });
});
