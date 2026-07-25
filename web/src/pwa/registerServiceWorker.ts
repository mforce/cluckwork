// #142 — service-worker registration, guarded.
//
// Registered by hand rather than through vite-plugin-pwa's injected snippet so
// the guards below are ours and testable.
//
// TWO things can be absent and neither is an error:
//   - `navigator.serviceWorker` only exists in a SECURE CONTEXT (https, or
//     localhost). Barn phones currently reach this app over plain http, where
//     the property is simply undefined. Touching it unguarded is the same class
//     of assumption that produced the #138 black screen, so every access is
//     behind a capability check and the whole thing no-ops quietly off-https.
//   - The build may not have emitted a worker (dev server, or a test run).
//
// The contract: calling this never throws and never rejects. The worst outcome
// is "no service worker", which is exactly today's behaviour.

export type UpdateHandler = (activate: () => Promise<void>) => void;

// Where vite-plugin-pwa emits the generated worker.
const SW_URL = "/sw.js";

// How long to wait for a SKIP_WAITING'd worker to actually take control before
// giving up and reloading anyway. Without a bound, a worker that never fires
// `controllerchange` — a browser quirk, a worker that failed to activate —
// leaves the caller's promise pending forever, which in the UI is a permanently
// disabled "Reloading…" button the user cannot escape (#142 review).
const ACTIVATION_TIMEOUT_MS = 5_000;

// True only where the SW API is actually usable. `isSecureContext` covers the
// https-or-localhost rule without hard-coding hostnames.
export function serviceWorkerSupported(): boolean {
  return (
    typeof navigator !== "undefined" &&
    "serviceWorker" in navigator &&
    typeof globalThis.isSecureContext === "boolean" &&
    globalThis.isSecureContext
  );
}

/**
 * Registers the worker and reports when a NEW version is waiting.
 *
 * `onUpdateReady` is handed an `activate` callback. Nothing is swapped until
 * that callback runs — the waiting worker keeps waiting — so an in-progress
 * daily entry is never interrupted by a deploy (#142: registerType "prompt").
 *
 * Returns the registration, or null when the environment can't support one.
 */
export async function registerServiceWorker(
  onUpdateReady?: UpdateHandler,
  signal?: AbortSignal,
): Promise<ServiceWorkerRegistration | null> {
  if (!serviceWorkerSupported()) return null;

  try {
    const registration = await navigator.serviceWorker.register(SW_URL, { scope: "/" });

    // A worker already parked in `waiting` means an update downloaded during a
    // previous visit and is still pending — surface it immediately.
    if (registration.waiting) notify(registration, onUpdateReady);

    // An update can ALREADY be installing by the time register() resolves: the
    // browser fires `updatefound` during registration, before there is anywhere
    // to attach a listener. Watching only future `updatefound` events would miss
    // it, and a long-lived tab would then sit on a stale build with no prompt
    // until its next full load (#142 review).
    if (registration.installing) watch(registration, registration.installing, onUpdateReady, signal);

    registration.addEventListener(
      "updatefound",
      () => {
        if (registration.installing)
          watch(registration, registration.installing, onUpdateReady, signal);
      },
      { signal },
    );

    return registration;
  } catch {
    // Registration can fail for reasons entirely outside the app's control — a
    // CSP that forbids workers, a proxy rewriting /sw.js, private-mode storage
    // rules. None of them should break the page that is already running.
    return null;
  }
}

// Workers already being watched. The initial-`installing` and `updatefound`
// paths can both land on the SAME worker (updatefound fires for the very worker
// that was already installing when register() resolved), and watching it twice
// announces the update twice.
const watched = new WeakSet<ServiceWorker>();

// Announces `worker` once it finishes installing. Shared by both entry points
// so the initial-installing and later-updatefound paths cannot drift apart.
function watch(
  registration: ServiceWorkerRegistration,
  worker: ServiceWorker,
  onUpdateReady?: UpdateHandler,
  signal?: AbortSignal,
) {
  if (watched.has(worker)) return;
  watched.add(worker);

  const check = () => {
    // `installed` WITH an existing controller means this is an update to an
    // already-running app, not the very first install. Without that check a
    // first-time visitor would be told to reload the version they just loaded.
    if (worker.state === "installed" && navigator.serviceWorker.controller)
      notify(registration, onUpdateReady);
  };
  // It may have finished installing between register() resolving and this call.
  check();
  worker.addEventListener("statechange", check, { signal });
}

function notify(registration: ServiceWorkerRegistration, onUpdateReady?: UpdateHandler) {
  if (!onUpdateReady) return;
  onUpdateReady(() => activateUpdate(registration));
}

/**
 * Applies a waiting update: tell the waiting worker to take over, then reload
 * once it actually controls the page.
 *
 * The reload is driven by `controllerchange` rather than fired straight after
 * SKIP_WAITING, because activation is asynchronous — reloading immediately can
 * land back on the OLD controller and leave the user still on the stale build,
 * having been told they were updated.
 *
 * That wait is bounded. If the worker never takes control we reload anyway:
 * a reload on the old version is recoverable (the prompt simply returns), while
 * a promise that never settles is a dead button.
 */
export async function activateUpdate(
  registration: ServiceWorkerRegistration,
): Promise<void> {
  const waiting = registration.waiting;
  if (!waiting) {
    // Nothing pending (already activated, or the user tapped twice) — a plain
    // reload is still the honest response to "reload".
    globalThis.location?.reload();
    return;
  }

  await new Promise<void>((resolve) => {
    // One AbortController tears down every listener however this settles.
    const teardown = new AbortController();
    let settled = false;
    const finish = () => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      teardown.abort();
      resolve();
    };
    const timer = setTimeout(finish, ACTIVATION_TIMEOUT_MS);

    // The normal signal: the new worker took control of this page.
    navigator.serviceWorker.addEventListener("controllerchange", finish, {
      signal: teardown.signal,
    });
    // …but an UNCONTROLLED page never gets `controllerchange` at all (the very
    // first worker doesn't claim existing clients, and clientsClaim is off).
    // Watching the worker's own state means those cases settle as soon as it is
    // actually running, instead of always burning the full timeout. `redundant`
    // means it failed and no reload will help it, but reloading is still the
    // honest answer to the button that was pressed.
    waiting.addEventListener(
      "statechange",
      () => {
        if (waiting.state === "activated" || waiting.state === "redundant") finish();
      },
      { signal: teardown.signal },
    );

    waiting.postMessage({ type: "SKIP_WAITING" });
  });

  globalThis.location?.reload();
}
