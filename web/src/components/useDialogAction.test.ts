import { act, renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { useDialogAction } from "./useDialogAction";

// #703 — the composed wrapper, away from any screen. SalesPage's tests prove the
// behaviour a user sees; these pin the four ordering rules a screen is entitled
// to rely on when it adopts the hook (handoff §5), so a later reader can change
// one without re-deriving the other.
function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const DIALOGS = ["create"] as const;

describe("useDialogAction", () => {
  it("claims the session before the action awaits, so a dismissal mid-flight supersedes it", async () => {
    const { result } = renderHook(() => useDialogAction(DIALOGS));
    act(() => result.current.openDialog("create"));
    const gate = deferred<void>();
    let seen: boolean | undefined;
    let flight!: Promise<void | undefined>;
    act(() => {
      flight = result.current.run("create", async (current) => {
        await gate.promise;
        seen = current();
      });
    });
    // The user gives up and starts again while the write is still out.
    act(() => result.current.dismissDialog("create"));
    act(() => result.current.openDialog("create"));
    await act(async () => {
      gate.resolve();
      await flight;
    });

    expect(seen).toBe(false);
  });

  it("keeps a claim current while nothing supersedes it", async () => {
    const { result } = renderHook(() => useDialogAction(DIALOGS));
    act(() => result.current.openDialog("create"));
    let seen: boolean | undefined;
    await act(async () => {
      await result.current.run("create", async (current) => {
        await Promise.resolve();
        seen = current();
      });
    });

    expect(seen).toBe(true);
  });

  it("treats a non-dialog scope as always current, whatever begins mid-flight", async () => {
    // A panel action has no session to be superseded by. Gating it would be
    // #703's PR 5 question, answered there — here it must behave exactly as it
    // did before the hook existed.
    const { result } = renderHook(() => useDialogAction(DIALOGS));
    const gate = deferred<void>();
    let seen: boolean | undefined;
    let flight!: Promise<void | undefined>;
    act(() => {
      flight = result.current.run("confirm", async (current) => {
        await gate.promise;
        seen = current();
      });
    });
    act(() => result.current.openDialog("confirm"));
    act(() => result.current.dismissDialog("confirm"));
    await act(async () => {
      gate.resolve();
      await flight;
    });

    expect(seen).toBe(true);
  });

  it("reports a failure to the dialog's own slot, and a non-dialog failure to the page", async () => {
    const { result } = renderHook(() => useDialogAction(DIALOGS));
    await act(async () => {
      await result.current.run("create", async () => { throw new Error("dialog boom"); });
    });
    expect(result.current.errors.forDialog("create")).toBe("dialog boom");
    expect(result.current.errors.page).toBeNull();

    await act(async () => {
      await result.current.run("confirm", async () => { throw new Error("page boom"); });
    });
    expect(result.current.errors.page).toBe("page boom");
    expect(result.current.errors.forDialog("create")).toBe("dialog boom");
  });

  it("drops a failure that lands after its dialog was dismissed", async () => {
    // #479's half: the verdict of an attempt the user gave up on has nowhere
    // honest to land — not in the dialog they reopened, not on the page.
    const { result } = renderHook(() => useDialogAction(DIALOGS));
    act(() => result.current.openDialog("create"));
    const gate = deferred<void>();
    let flight!: Promise<void | undefined>;
    act(() => {
      flight = result.current.run("create", async () => {
        await gate.promise;
        throw new Error("abandoned boom");
      });
    });
    act(() => result.current.dismissDialog("create"));
    act(() => result.current.openDialog("create"));
    await act(async () => {
      gate.reject(new Error("unused"));
      await flight;
    });

    expect(result.current.errors.forDialog("create")).toBeUndefined();
    expect(result.current.errors.page).toBeNull();
  });

  it("drops a failure that lands after its dialog was reopened without a dismiss", async () => {
    // codex + CodeRabbit, round 1 of #703 PR 1: a screen can close a dialog by
    // a route that never calls dismiss — a parent swapping the record, a role
    // change, a route change — and then open it again. Ending the session on
    // open was pinned below; muting the attempt still out was not, so its
    // failure landed in the dialog the user had just opened. Both edges are
    // one operation now.
    const { result } = renderHook(() => useDialogAction(DIALOGS));
    act(() => result.current.openDialog("create"));
    const gate = deferred<void>();
    let flight!: Promise<void | undefined>;
    act(() => {
      flight = result.current.run("create", async () => {
        await gate.promise;
        throw new Error("stale boom");
      });
    });
    act(() => result.current.openDialog("create")); // reopened, never dismissed
    await act(async () => {
      gate.resolve();
      await flight;
    });

    expect(result.current.errors.forDialog("create")).toBeUndefined();
    expect(result.current.errors.page).toBeNull();
  });

  it("leaves the previous message alone when the in-flight guard skips a run", async () => {
    // beginAttempt runs INSIDE the guarded action: a press that the guard
    // rejects must not blank the verdict the dialog is still showing.
    const { result } = renderHook(() => useDialogAction(DIALOGS));
    await act(async () => {
      await result.current.run("create", async () => { throw new Error("still showing"); });
    });
    expect(result.current.errors.forDialog("create")).toBe("still showing");

    const gate = deferred<void>();
    let flight!: Promise<void | undefined>;
    act(() => {
      flight = result.current.run("confirm", () => gate.promise);
    });
    let skipped!: Promise<string | undefined>;
    act(() => {
      skipped = result.current.run("create", async () => "ran anyway");
    });
    await expect(skipped).resolves.toBeUndefined();
    expect(result.current.errors.forDialog("create")).toBe("still showing");

    await act(async () => {
      gate.resolve();
      await flight;
    });
  });

  it("resolves the action's value on success, and undefined when it throws", async () => {
    const { result } = renderHook(() => useDialogAction(DIALOGS));
    let ok!: Promise<number | undefined>;
    await act(async () => {
      ok = result.current.run("confirm", async () => 42);
      await ok;
    });
    await expect(ok).resolves.toBe(42);

    let failed!: Promise<number | undefined>;
    await act(async () => {
      failed = result.current.run("confirm", async () => { throw new Error("nope"); });
      await failed;
    });
    await expect(failed).resolves.toBeUndefined();
  });

  it("calls onAttempt once per attempt that actually runs", async () => {
    let calls = 0;
    const { result } = renderHook(() => useDialogAction(DIALOGS, { onAttempt: () => { calls += 1; } }));
    await act(async () => {
      await result.current.run("create", async () => undefined);
    });
    expect(calls).toBe(1);

    const gate = deferred<void>();
    let flight!: Promise<void | undefined>;
    act(() => {
      flight = result.current.run("confirm", () => gate.promise);
    });
    act(() => {
      void result.current.run("create", async () => undefined); // skipped by the guard
    });
    await act(async () => {
      gate.resolve();
      await flight;
    });
    expect(calls).toBe(2);
  });

  it("claims the session before onAttempt runs, so an onAttempt that ends the session supersedes the attempt", async () => {
    // codex, round 1 of #703 PR 1: the test above only counts calls, so moving
    // `onAttempt` ahead of the claim left every assertion green. The order is
    // observable exactly one way — an onAttempt that itself ends the session
    // (a screen resetting the dialog it is about) must leave THIS attempt
    // superseded, rather than letting the attempt claim the session it created.
    let api: ReturnType<typeof useDialogAction> | undefined;
    const { result } = renderHook(() =>
      useDialogAction(DIALOGS, { onAttempt: () => api?.openDialog("create") }));
    api = result.current;
    let seen: boolean | undefined;
    await act(async () => {
      await result.current.run("create", async (current) => { seen = current(); });
    });

    expect(seen).toBe(false);
  });

  it("ends the session on open as well as on dismiss", async () => {
    // Both edges end whatever session was on screen. A screen that only ended
    // it on dismiss would let a dialog closed by some other route — a parent
    // re-render, a route change — keep an attempt current across a reopen.
    const { result } = renderHook(() => useDialogAction(DIALOGS));
    act(() => result.current.openDialog("create"));
    const gate = deferred<void>();
    let seen: boolean | undefined;
    let flight!: Promise<void | undefined>;
    act(() => {
      flight = result.current.run("create", async (current) => {
        await gate.promise;
        seen = current();
      });
    });
    act(() => result.current.openDialog("create")); // reopened without a dismiss
    await act(async () => {
      gate.resolve();
      await flight;
    });

    expect(seen).toBe(false);
  });
});
