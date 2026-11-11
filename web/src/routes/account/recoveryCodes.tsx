import { createContext, useCallback, useContext, useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from "react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Box, Button, Checkbox, DialogActions, FormControlLabel, Stack, Typography } from "@mui/material";
import { currentSessionGeneration, isSignedOut } from "../../api/client";
import { onSessionChangeElsewhere } from "../../auth/sessionSignal";
import { useAuth } from "../../auth/useAuth";
import { Dialog } from "../../components/Dialog";
import { useCopy } from "../../components/useCopy";
import { MAIN_CONTENT_ID } from "../../lib/mainContent";
import { saveBlob } from "../../lib/saveBlob";

// #1277 (maintainer, 2026-11-05: "Keep codes above pages"): recovery codes
// that are shown once live here, above the routes, until the user saves them,
// and so does the request that will hand them out. A confirm or regenerate
// that settles after its page is gone still lands here, and no navigation drops
// the set: only Done, the end of this tab's session, or a sign-in or sign-out
// in another tab (#1398). In memory only: never in storage, a URL or a log.
interface HeldRecoveryCodes {
  // Keys the dialog, so a new set never shows the last set's notes.
  id: number;
  codes: readonly string[];
  regenerated: boolean;
  // Part of the value, so a new set starts unticked.
  saved: boolean;
  // For the download's title; the session's Me lives below this slot.
  email: string;
}

// One code-issuing request. deliver shows its set unless the request's session
// has ended since; release, from a finally, ends the request either way.
export interface Delivery {
  // Still the active request, in the session that started it.
  live: () => boolean;
  deliver: (codes: readonly string[], set: { regenerated: boolean; email: string }) => void;
  release: () => void;
}

interface Slot {
  // A request is out or a set is held: Set up, Replace and Regenerate wait.
  busy: boolean;
  // Counts the requests that have ended, so the status can reload on each.
  settled: number;
  // Null while busy, so only one request can issue codes at a time. Call it
  // before the step-up proof: it reads the session generation then.
  expect: () => Delivery | null;
}

const RecoveryCodesContext = createContext<Slot | null>(null);

// Here rather than in SecurityPanel: this module is in the main bundle, and
// importing it from the lazy Account chunk is the only direction that keeps
// the two apart.
export const SECURITY_SUMMARY_ID = "account-security";

export function useRecoveryCodesSlot(): Slot {
  const slot = useContext(RecoveryCodesContext);
  if (!slot) throw new Error("useRecoveryCodesSlot needs a RecoveryCodesProvider above it.");
  return slot;
}

export function RecoveryCodesProvider({ children }: { children: ReactNode }) {
  const { isAuthenticated } = useAuth();
  // State renders the slot; the refs answer expect and deliver, which run
  // between renders.
  const [held, setHeld] = useState<HeldRecoveryCodes | null>(null);
  const [pending, setPending] = useState<number | null>(null);
  const [settled, setSettled] = useState(0);
  const heldRef = useRef<HeldRecoveryCodes | null>(null);
  const pendingRef = useRef<number | null>(null);
  const tickets = useRef(0);
  const put = (set: HeldRecoveryCodes | null) => { heldRef.current = set; setHeld(set); };
  const putPending = (ticket: number | null) => { pendingRef.current = ticket; setPending(ticket); };

  // #308 — the set, and the request it waits on, end with this tab's session.
  // The clear runs before paint and overwrites anything that arrived earlier.
  // A layout effect is the safe side; no jsdom test can tell it from a
  // passive one.
  useLayoutEffect(() => {
    if (isAuthenticated) return;
    put(null);
    putPending(null);
  }, [isAuthenticated]);

  const asks = pending !== null || (held !== null && !held.saved);
  useEffect(() => {
    if (!asks) return;
    const warn = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      e.returnValue = "";
    };
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [asks]);

  // No deps: put and putPending must stay ref-and-setter only, so this
  // callback never closes over a render's state.
  const expect = useCallback((): Delivery | null => {
    // #1277 — no code-issuing work starts once sign-out has begun.
    if (isSignedOut()) return null;
    // Unreachable from the UI, which disables its triggers while busy; the
    // slot-contract test pins it.
    if (pendingRef.current !== null || heldRef.current !== null) return null;
    const ticket = ++tickets.current;
    const generation = currentSessionGeneration();
    putPending(ticket);
    const current = () => pendingRef.current === ticket && generation === currentSessionGeneration();
    return {
      live: current,
      deliver: (codes, { regenerated, email }) => {
        if (current()) put({ id: ticket, codes, regenerated, email, saved: false });
      },
      release: () => {
        if (pendingRef.current !== ticket) return;
        putPending(null);
        setSettled((n) => n + 1);
      },
    };
  }, []);

  // The trigger that opened the dialog is gone by now, so focus goes to the
  // Security section if it is on screen, else to the page's content.
  const done = useCallback(() => {
    put(null);
    requestAnimationFrame(() => (document.getElementById(SECURITY_SUMMARY_ID) ?? document.getElementById(MAIN_CONTENT_ID))?.focus());
  }, []);

  // #1398 — a sign-in or sign-out in another tab drops a held set, as Done
  // does. With nothing held it does nothing, so focus stays where it is.
  // Only the set: this tab's session, its request and its page stay.
  useEffect(() => onSessionChangeElsewhere(() => { if (heldRef.current) done(); }), [done]);

  const slot = useMemo(() => ({ busy: pending !== null || held !== null, settled, expect }), [pending, held, settled, expect]);
  return (
    <RecoveryCodesContext.Provider value={slot}>
      {children}
      {held && (
        <RecoveryCodesDialog key={held.id} held={held}
          onSaved={(saved) => put(heldRef.current && { ...heldRef.current, saved })} onDone={done} />
      )}
    </RecoveryCodesContext.Provider>
  );
}

// #1275 "Recovery codes", shown once. The maintainer chose a locked dialog
// (2026-11-05): until the codes are saved nothing dismisses it, and the modal
// keeps the sidebar, the bottom nav and Help out of reach.
function RecoveryCodesDialog({ held, onSaved, onDone }: {
  held: HeldRecoveryCodes;
  onSaved: (saved: boolean) => void;
  onDone: () => void;
}) {
  const { t } = useTranslation("account");
  const { copy, notice, setNote } = useCopy();
  const hintId = useId();
  const text = held.codes.join("\n");

  const download = () => {
    saveBlob(new Blob([`${t("codesFileTitle", { email: held.email })}\n\n${text}\n`], { type: "text/plain" }), "cluckwork-recovery-codes.txt");
    setNote({ text: t("codesDownloaded"), ok: true });
  };

  return (
    <Dialog
      open
      title={t("codesHeading")}
      onClose={onDone}
      closeDisabled={!held.saved}
      describedBy={hintId}
      fullScreenOnPhone
      actions={(
        <DialogActions>
          <Button variant="contained" disabled={!held.saved} onClick={onDone}>{t("doneButton")}</Button>
        </DialogActions>
      )}
    >
      <Stack spacing={2}>
        <Typography>{t("codesOn")}</Typography>
        {held.regenerated && <Alert severity="warning" role="note">{t("codesReplaced")}</Alert>}
        <Typography color="text.secondary">{t("codesHint")}</Typography>
        <Box component="ul" aria-label={t("codesListLabel")} sx={{
          listStyle: "none", m: 0, p: 2, display: "grid", gap: 1, gridTemplateColumns: { xs: "1fr", sm: "1fr 1fr" },
          border: "1px solid var(--rule)", borderRadius: 1, fontFamily: "monospace", fontSize: "1.05rem",
        }}>
          {held.codes.map((c) => <li key={c}>{c}</li>)}
        </Box>
        <Stack direction={{ xs: "column", sm: "row" }} spacing={1.5}>
          <Button variant="outlined" onClick={() => void copy(text, t("codesCopied"), t("copyFailed"))}>{t("copyCodesButton")}</Button>
          <Button variant="outlined" onClick={download}>{t("downloadButton")}</Button>
        </Stack>
        {notice}
        <Typography id={hintId} color="text.secondary">{t("codesLockedHint")}</Typography>
        <FormControlLabel control={<Checkbox checked={held.saved} onChange={(e) => onSaved(e.target.checked)} />} label={t("savedCheckbox")} />
      </Stack>
    </Dialog>
  );
}
