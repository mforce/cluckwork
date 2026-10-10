import { useCallback, useEffect, useEffectEvent, useRef, useState } from "react";

// #1185 — the supported way for a screen to READ data for a set of inputs
// (paged lists use usePagedList; dialog writes use useDialogAction). A raw
// `useEffect(() => { load().then(set) })` lets a response for inputs the user
// has already left land after the newer one and overwrite it (#467, #703,
// #918), and the lint in web/lint/eslint.config.js now refuses that shape.
//
// Each run owns an AbortController, which is also its generation: inputs
// changing, `retry` or unmount abort it, and an aborted run touches no state,
// in its failure path as much as its success path. `retry` aborts at once, not
// when the replacement effect runs: a retry after an awaited save must drop a
// response that lands in between. A `load` that throws before returning a
// promise is reported like a rejection. `load` is read through an
// effect event, so an inline arrow does not reload on every render; only
// `inputs` and `retry` do.
//
// `data` is kept while a newer run loads (`loading` says it may belong to
// the inputs being left) and cleared by a failure: empty is honest, stale is
// not.
export function useLatestLoad<T>(
  inputs: readonly unknown[],
  load: (signal: AbortSignal) => Promise<T>,
): { data: T | null; error: unknown; loading: boolean; retry: () => void } {
  const [state, setState] = useState<{ data: T | null; error: unknown; loading: boolean }>(
    { data: null, error: null, loading: true },
  );
  const [attempt, setAttempt] = useState(0);
  const run = useEffectEvent(load);
  const active = useRef<AbortController | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    active.current = controller;
    setState((prev) => ({ data: prev.data, error: null, loading: true }));
    // eslint-disable-next-line no-restricted-syntax -- this hook is the guard the rule points to
    new Promise<T>((resolve) => resolve(run(controller.signal))).then(
      (data) => { if (!controller.signal.aborted) setState({ data, error: null, loading: false }); },
      (error: unknown) => { if (!controller.signal.aborted) setState({ data: null, error, loading: false }); },
    );
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the caller's inputs are the keys
  }, [...inputs, attempt]);

  const retry = useCallback(() => {
    active.current?.abort();
    setAttempt((n) => n + 1);
  }, []);
  return { ...state, retry };
}
