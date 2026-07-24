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
): Promise<ServiceWorkerRegistration | null> {
  if (!serviceWorkerSupported()) return null;

  try {
    const registration = await navigator.serviceWorker.register(SW_URL, { scope: "/" });

    // A worker already parked in `waiting` means an update downloaded during a
    // previous visit and is still pending — surface it immediately.
    if (registration.waiting) notify(registration, onUpdateReady);

    registration.addEventListener("updatefound", () => {
      const installing = registration.installing;
      if (!installing) return;
      installing.addEventListener("statechange", () => {
        // `installed` WITH an existing controller means this is an update to an
        // already-running app, not the very first install. Without that check a
        // first-time visitor would be told to reload the version they just
        // loaded.
        if (installing.state === "installed" && navigator.serviceWorker.controller)
          notify(registration, onUpdateReady);
      });
    });

    return registration;
  } catch {
    // Registration can fail for reasons entirely outside the app's control — a
    // CSP that forbids workers, a proxy rewriting /sw.js, private-mode storage
    // rules. None of them should break the page that is already running.
    return null;
  }
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
    const done = () => resolve();
    navigator.serviceWorker.addEventListener("controllerchange", done, { once: true });
    waiting.postMessage({ type: "SKIP_WAITING" });
  });

  globalThis.location?.reload();
}
