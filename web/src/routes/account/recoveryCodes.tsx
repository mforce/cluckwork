import { createContext, useCallback, useContext, useEffect, useId, useRef, useState } from "react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Box, Button, Checkbox, DialogActions, FormControlLabel, Stack, Typography } from "@mui/material";
import { currentSessionGeneration } from "../../api/client";
import { useAuth } from "../../auth/useAuth";
import { Dialog } from "../../components/Dialog";
import { saveBlob } from "../../lib/saveBlob";

// #1277 MC4 (maintainer, 2026-11-05: "Keep codes above pages"): recovery codes
// that are shown once live here, above the routes, until the user saves them.
// A confirm or regenerate that settles after its page is gone still lands
// here, and no navigation drops the set: only Done, or the end of the session.
// In memory only: never in storage, a URL or a log.
export interface UnsavedRecoveryCodes {
  codes: readonly string[];
  regenerated: boolean;
  // Part of the value, so a new set starts unticked.
  saved: boolean;
  // For the download's title; the session's Me lives below this slot.
  email: string;
}

type Hold = (codes: readonly string[], set: { regenerated: boolean; email: string; generation: number }) => void;

const RecoveryCodesContext = createContext<Hold | null>(null);

export const SECURITY_SUMMARY_ID = "account-security";
const MAIN_CONTENT_ID = "main-content";

// Shows a set the server just handed out. `generation` is the session's when
// the request was sent: a set that lands after that session ended is dropped.
export function useHoldRecoveryCodes(): Hold {
  const hold = useContext(RecoveryCodesContext);
  if (!hold) throw new Error("useHoldRecoveryCodes needs a RecoveryCodesProvider above it.");
  return hold;
}

export function RecoveryCodesProvider({ children }: { children: ReactNode }) {
  const { isAuthenticated } = useAuth();
  const [held, setHeld] = useState<UnsavedRecoveryCodes | null>(null);
  const authenticated = useRef(isAuthenticated);

  // #308 — the codes end with the session that received them.
  useEffect(() => {
    authenticated.current = isAuthenticated;
    if (!isAuthenticated) setHeld(null);
  }, [isAuthenticated]);

  const unsaved = held !== null && !held.saved;
  useEffect(() => {
    if (!unsaved) return;
    const warn = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      e.returnValue = "";
    };
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [unsaved]);

  const hold = useCallback<Hold>((codes, { regenerated, email, generation }) => {
    if (!authenticated.current || generation !== currentSessionGeneration()) return;
    setHeld({ codes, regenerated, email, saved: false });
  }, []);

  // The trigger that opened the dialog is gone by now, so focus goes to the
  // Security section if it is on screen, else to the page's content.
  const done = () => {
    setHeld(null);
    requestAnimationFrame(() => (document.getElementById(SECURITY_SUMMARY_ID) ?? document.getElementById(MAIN_CONTENT_ID))?.focus());
  };

  return (
    <RecoveryCodesContext.Provider value={hold}>
      {children}
      {held && (
        <RecoveryCodesDialog held={held} onSaved={(saved) => setHeld((h) => h && { ...h, saved })} onDone={done} />
      )}
    </RecoveryCodesContext.Provider>
  );
}

// Copy and download feedback, announced in one live region.
export function useCopy() {
  const [note, setNote] = useState<{ text: string; ok: boolean } | null>(null);
  // Started inside the promise: a plain-http origin has no clipboard at all.
  const copy = (text: string, done: string, failed: string) =>
    Promise.resolve().then(() => navigator.clipboard.writeText(text))
      .then(() => setNote({ text: done, ok: true }), () => setNote({ text: failed, ok: false }));
  const notice: ReactNode = (
    <Box role="status" sx={{ "&:empty": { display: "none" } }}>
      {note && <Alert severity={note.ok ? "success" : "warning"} role="none">{note.text}</Alert>}
    </Box>
  );
  return { copy, notice, setNote };
}

// #1275 "Recovery codes", shown once. The maintainer chose a locked dialog
// (2026-11-05): until the codes are saved nothing dismisses it, and the modal
// keeps the sidebar, the bottom nav and Help out of reach.
function RecoveryCodesDialog({ held, onSaved, onDone }: {
  held: UnsavedRecoveryCodes;
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
