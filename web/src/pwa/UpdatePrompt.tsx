import { useEffect, useState } from "react";
import { registerServiceWorker } from "./registerServiceWorker";

// #142 — "a new version is ready" affordance.
//
// The service worker is registered with registerType "prompt", so a downloaded
// update sits in `waiting` and changes nothing until the user accepts. That is
// deliberate: this app is used to type daily entries on barn phones, and an
// automatic swap can discard a half-filled form. The cost of that choice is this
// component — without it an update would wait forever and clients would silently
// run a stale shell against a newer API.
//
// Renders nothing at all until an update is genuinely waiting, so it costs a
// hook and no layout in the normal case.
export function UpdatePrompt() {
  // Holds the activator handed over by the registration; its presence IS the
  // "update ready" state.
  const [activate, setActivate] = useState<(() => Promise<void>) | null>(null);
  const [dismissed, setDismissed] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let cancelled = false;
    // Never rejects — off a secure context it resolves to null and we simply
    // never hear about an update.
    void registerServiceWorker((activateUpdate) => {
      if (cancelled) return;
      // Stored via an updater fn: React would otherwise CALL a bare function
      // passed to a setter and store its result.
      setActivate(() => activateUpdate);
      // A newly-arrived update re-earns the user's attention even if an earlier
      // one was dismissed this session.
      setDismissed(false);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  if (!activate || dismissed) return null;

  async function onReload() {
    if (busy || !activate) return;
    setBusy(true);
    try {
      await activate(); // resolves into a page reload
    } catch {
      // If activation fails the old app keeps working; let the user retry
      // rather than leaving a dead spinner.
      setBusy(false);
    }
  }

  return (
    // polite + status: announced by a screen reader without stealing focus from
    // whatever is being typed.
    <div className="update-banner" role="status" aria-live="polite">
      <span className="update-banner-text">A new version of Cluckwork is ready.</span>
      <div className="update-banner-actions">
        <button type="button" onClick={onReload} disabled={busy}>
          {busy ? "Reloading…" : "Reload"}
        </button>
        <button
          type="button"
          className="update-banner-later"
          onClick={() => setDismissed(true)}
          disabled={busy}
        >
          Later
        </button>
      </div>
    </div>
  );
}
