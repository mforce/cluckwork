import { useCallback, useEffect, useState } from "react";
import { ApiError, NO_PROOF, stepUp } from "../../api/client";
import type { StepUpProof } from "../../api/client";
import { useAuth } from "../../auth/useAuth";
import i18n from "../../i18n";

// The stale-session refusal changed nothing on the server (#1280), and the API
// layer never resends it, so the user only needs to try again.
export function stale(err: unknown): unknown {
  return err instanceof ApiError && err.status === 401 && err.title === "Auth.CredentialsSuperseded"
    ? new Error(i18n.t("account:sessionChanged"))
    : err;
}

// #1277/#1284 — the Security section's step-up dialogs: which one is open and
// the proof typed into it. The proof never outlives the dialog or the session
// (#308): every way out clears it (cancel, send, logout), so opening does not.
export function useStepUpDialog<K extends string>({ openDialog, dismissDialog }: {
  openDialog: (scope: K) => void;
  dismissDialog: (scope: K) => void;
}) {
  const { isAuthenticated } = useAuth();
  const [action, setAction] = useState<K | null>(null);
  const [proof, setProof] = useState<StepUpProof>(NO_PROOF);

  useEffect(() => {
    if (isAuthenticated) return;
    setProof(NO_PROOF);
    setAction(null);
  }, [isAuthenticated]);

  const open = useCallback((next: K) => {
    openDialog(next);
    setAction(next);
  }, [openDialog]);

  function close() {
    setProof(NO_PROOF);
    setAction(null);
    if (action) dismissDialog(action);
  }

  // The step-up proof the dialog sends first; the grant it returns is spent once.
  async function prove() {
    const entered = proof;
    setProof(NO_PROOF);
    return (await stepUp(entered)).token;
  }

  return { action, proof, setProof, open, close, prove };
}

export type StepUpDialogState<K extends string> = ReturnType<typeof useStepUpDialog<K>>;
