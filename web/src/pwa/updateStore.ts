import { useSyncExternalStore } from "react";
import { registerServiceWorker } from "./registerServiceWorker";

export interface UpdateState {
  /** Holds the activator handed over by the registration; its presence IS the "update ready" state. */
  activate: (() => Promise<void>) | null;
  dismissed: boolean;
  busy: boolean;
}

const INITIAL_STATE: UpdateState = { activate: null, dismissed: false, busy: false };

// #936 — one service-worker registration, one source of truth for
// pending/deferred state, shared between UpdatePrompt (which owns the only
// registerServiceWorker call) and the More-menu recovery action (which only
// ever reads and dispatches against this store). A plain module singleton
// rather than React context: the two consumers have no provider in common —
// UpdatePrompt mounts outside auth/router/FarmProvider on purpose (#142), the
// menu lives deep inside AppLayout.
let state: UpdateState = INITIAL_STATE;
let watching = false;
const listeners = new Set<() => void>();

function setState(patch: Partial<UpdateState>): void {
  state = { ...state, ...patch };
  for (const listener of listeners) listener();
}

export function subscribeToUpdateState(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function getUpdateState(): UpdateState {
  return state;
}

/**
 * Starts the app's one-and-only service-worker registration. Called from
 * UpdatePrompt's own effect; idempotent so StrictMode's dev-mode effect
 * replay (mount/unmount/remount) does not register twice.
 */
export function startUpdateWatch(signal: AbortSignal): void {
  if (watching) return;
  watching = true;
  void registerServiceWorker((activateWorker) => {
    if (signal.aborted) return;
    // A newly-arrived update re-earns the user's attention even if an
    // earlier one was dismissed this session.
    setState({ activate: activateWorker, dismissed: false });
  }, signal);
  signal.addEventListener("abort", () => { watching = false; }, { once: true });
}

/** Later on the overlay: hides it, but leaves the menu recovery action available. */
export function dismissUpdate(): void {
  setState({ dismissed: true });
}

/** The More-menu recovery action: reopens the same overlay (#936). */
export function reopenUpdate(): void {
  setState({ dismissed: false });
}

export async function activateWaitingUpdate(): Promise<void> {
  if (state.busy || !state.activate) return;
  setState({ busy: true });
  try {
    await state.activate(); // resolves into a page reload
  } catch {
    // If activation fails the old app keeps working; let the user retry
    // rather than leaving a dead spinner.
    setState({ busy: false });
  }
}

/**
 * Whether an update is currently waiting, regardless of the overlay's own
 * dismissed flag — the More-menu recovery action (#936) reads this alone so
 * it stays visible while the overlay itself is hidden after Later.
 */
export function useIsUpdateWaiting(): boolean {
  const current = useSyncExternalStore(subscribeToUpdateState, getUpdateState);
  return current.activate !== null;
}

export function resetUpdateStoreForTests(): void {
  state = INITIAL_STATE;
  watching = false;
  listeners.clear();
}
