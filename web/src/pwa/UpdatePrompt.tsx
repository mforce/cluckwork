import { useEffect, useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import { useMissedAnnouncement } from "../components/useMissedAnnouncement";
import { useCachedBannerUrl } from "../lib/bannerCache";
import { getBoundFarmCode } from "../auth/tokenStore";
import { CURRENT_VERSION, describeVersionChange, fetchAvailableVersion } from "./appVersion";
import {
  activateWaitingUpdate, dismissUpdate, getUpdateState, startUpdateWatch, subscribeToUpdateState,
} from "./updateStore";

// #142/#936 — "a new version is ready" affordance.
//
// The service worker is registered with registerType "prompt", so a downloaded
// update sits in `waiting` and changes nothing until the user accepts. That is
// deliberate: this app is used to type daily entries on barn phones, and an
// automatic swap can discard a half-filled form. The cost of that choice is this
// component — without it an update would wait forever and clients would silently
// run a stale shell against a newer API.
//
// Renders no visible UI until an update is genuinely waiting — only the
// always-present offscreen region below, empty and occupying no layout until
// there is a missed announcement to make (#485).
//
// State (activate/dismissed/busy) lives in updateStore.ts, not local
// component state: the More-menu recovery action (AppLayout.tsx,
// BottomNav.tsx) reads and dispatches against the SAME store, so "Later"
// here and "Update available" there always agree, with exactly one
// registerServiceWorker call (#936).
export function UpdatePrompt() {
  const { t } = useTranslation("pwa");
  const state = useSyncExternalStore(subscribeToUpdateState, getUpdateState);
  const { busy } = state;

  useEffect(() => {
    // The controller removes the registration's own listeners on unmount.
    // Without it, StrictMode's dev-mode effect replay would leave a dead
    // `updatefound`/`statechange` listener behind on every remount (#142
    // review).
    const controller = new AbortController();
    startUpdateWatch(controller.signal);
    return () => controller.abort();
  }, []);

  const waiting = state.activate !== null && !state.dismissed;
  // #485 — the overlay below announces itself on the ordinary path. It cannot
  // when a dialog is open, because it is inert then and out of the
  // accessibility tree; this covers only that case.
  const missed = useMissedAnnouncement(waiting ? t("updateAvailable") : null);
  const availableVersion = useAvailableVersion(waiting);

  // Reused from the same device-local cache BrandSplash.tsx reads (#179):
  // no `/account` read, no FarmProvider — UpdatePrompt still works on login
  // and wherever farm data is unavailable (#142).
  const bannerUrl = useCachedBannerUrl(getBoundFarmCode() ?? "");

  const versionChange = describeVersionChange(CURRENT_VERSION, availableVersion);

  return (
    <>
      {/* Carries the announcement the overlay below could not make because a
          dialog had it inert (#485), and stays empty the rest of the time so
          the two never say the same sentence twice.

          `aria-live` + `aria-atomic` rather than `role="status"`, which is
          just shorthand for that pair: this element is always mounted, and a
          permanent node holding a live ROLE would answer every
          `getByRole("status"/"alert")` query in the app. Those roles mean "a
          message is on screen" here — ~20 error banners use `role="alert"` —
          and the E2E suite reads their absence as "nothing has gone wrong". */}
      <p className="sr-only" aria-live="polite" aria-atomic="true">{missed}</p>
      {waiting && (
        // Plain HTML, not a real MUI Dialog: nothing sets the rest of the
        // page `inert` while this is open (that would need a caller inside
        // SessionProvider — the very gap styles.css documents above
        // `.brand-splash-backdrop`), so claiming `role="dialog"`/
        // `aria-modal` here would assert a guarantee this component cannot
        // back. `role="status"` + `aria-live="polite"` is the same honest
        // contract the retired Snackbar/Alert made (#828).
        <div className="update-overlay-backdrop">
          <div
            className="update-overlay"
            role="status"
            aria-live="polite"
            style={{ boxShadow: "var(--shadow-bar)" }}
          >
            {bannerUrl !== null && (
              <img className="update-overlay-banner" src={bannerUrl} alt="" />
            )}
            <p className="update-overlay-title">{t("updateAvailable")}</p>
            {versionChange !== null && (
              <p className="update-overlay-version">
                {t("versionChange", { current: versionChange.current, available: versionChange.available })}
              </p>
            )}
            <div className="update-overlay-actions">
              {/* activateWaitingUpdate resolves into a page reload. */}
              <button type="button" onClick={() => void activateWaitingUpdate()} disabled={busy} aria-busy={busy}>
                {busy ? t("reloading") : t("reload")}
              </button>
              <button type="button" className="link" onClick={dismissUpdate} disabled={busy}>
                {t("later")}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

/**
 * Fetches the waiting build's version once an update is actually waiting
 * (see appVersion.ts for why this is a network side-channel rather than a
 * postMessage to the worker). Never guesses: a failed/malformed read leaves
 * this null, and the overlay simply omits the version line.
 */
function useAvailableVersion(waiting: boolean): string | null {
  const [version, setVersion] = useState<string | null>(null);

  useEffect(() => {
    if (!waiting) {
      setVersion(null);
      return;
    }
    const controller = new AbortController();
    void fetchAvailableVersion(controller.signal).then((value) => {
      if (!controller.signal.aborted) setVersion(value);
    });
    return () => controller.abort();
  }, [waiting]);

  return version;
}
