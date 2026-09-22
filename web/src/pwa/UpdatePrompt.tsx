import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Snackbar } from "@mui/material";
import { useMissedAnnouncement } from "../components/useMissedAnnouncement";
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
// Renders no visible UI until an update is genuinely waiting — only the
// always-present offscreen region below, empty and occupying no layout until
// there is a missed announcement to make (#485).
export function UpdatePrompt() {
  const { t } = useTranslation("pwa");
  // Holds the activator handed over by the registration; its presence IS the
  // "update ready" state.
  const [activate, setActivate] = useState<(() => Promise<void>) | null>(null);
  const [dismissed, setDismissed] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    // The controller both suppresses late state updates AND removes the
    // registration's own listeners on unmount. Without the latter, StrictMode's
    // dev-mode effect replay would leave a dead `updatefound`/`statechange`
    // listener behind on every remount (#142 review).
    const teardown = new AbortController();
    // Never rejects — off a secure context it resolves to null and we simply
    // never hear about an update.
    void registerServiceWorker((activateUpdate) => {
      if (teardown.signal.aborted) return;
      // Stored via an updater fn: React would otherwise CALL a bare function
      // passed to a setter and store its result.
      setActivate(() => activateUpdate);
      // A newly-arrived update re-earns the user's attention even if an earlier
      // one was dismissed this session.
      setDismissed(false);
    }, teardown.signal);
    return () => teardown.abort();
  }, []);

  const waiting = activate !== null && !dismissed;
  // #485 — the banner below announces itself on the ordinary path. It cannot
  // when a dialog is open, because it is inert then and out of the
  // accessibility tree; this covers only that case.
  const missed = useMissedAnnouncement(waiting ? t("updateAvailable") : null);

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
    <>
      {/* Carries the announcement the banner below could not make because a
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
        // #828 (review) — positioning is a plain `.update-banner-position`
        // styles.css class on Snackbar's OWN root (via `className`, which
        // Snackbar merges into its root slot's class list), not `sx`:
        // measured against the sim stack, Snackbar's `sx`-driven
        // `position`/`inset`/`zIndex` computed as `static`/`auto` at
        // runtime — this app's CSP-nonced Emotion cache
        // (FarmThemeProvider.tsx) blocked that specific style insertion
        // (`Applying inline style violates ... style-src ... nonce-...`).
        // A stylesheet class, loaded via the ordinary `<link
        // rel="stylesheet">` every other fixed-position float in this file
        // already relies on (`.brand-splash-backdrop`), is immune to it.
        // `open` is not driven by MUI's own close transition: this whole
        // tree mounts and unmounts through the `waiting &&` above, so React
        // removes it immediately, with no exit-animation delay, the same
        // instant swap the div it replaces gave Later/Reload.
        <Snackbar
          open
          anchorOrigin={{ vertical: "bottom", horizontal: "right" }}
          className="update-banner-position"
        >
          <Alert
            role="status"
            aria-live="polite"
            severity="info"
            icon={false}
            className="update-banner"
            sx={{
              boxShadow: "var(--shadow-bar)", alignItems: "center",
              "& .MuiAlert-action": { flexWrap: "wrap", rowGap: 0.5 },
            }}
            action={(
              <>
                <Button color="inherit" size="small" onClick={onReload} disabled={busy}>
                  {busy ? t("reloading") : t("reload")}
                </Button>
                <Button color="inherit" size="small" onClick={() => setDismissed(true)} disabled={busy}>
                  {t("later")}
                </Button>
              </>
            )}
          >
            {t("updateAvailable")}
          </Alert>
        </Snackbar>
      )}
    </>
  );
}
