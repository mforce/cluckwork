import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { getTwoFactorStatus, regenerateRecoveryCodes, startTwoFactor } from "../../api/cluckwork";
import type { EnrolmentStart } from "../../api/cluckwork";
import { ApiError, confirmTwoFactor, NO_PROOF, stepUp, turnOffTwoFactor } from "../../api/client";
import type { StepUpProof } from "../../api/client";
import { useAuth } from "../../auth/useAuth";
import { useDialogAction } from "../../components/useDialogAction";
import { useLatestLoad } from "../../components/useLatestLoad";
import { useShellLock } from "../../components/ShellLock";
import i18n from "../../i18n";
import { useHistoryHold } from "./useHistoryHold";

// #1277 — the Security section's two-factor authentication. Start,
// Regenerate and Turn off each step up in a dialog first; confirm is the
// enrolment screen's own form.
export type ProofAction = "start" | "regenerate" | "turn-off";
const SCOPES = ["start", "regenerate", "turn-off", "confirm"] as const;

// From this many recovery codes left, the section warns (#1274 decision 9).
export const LOW_RECOVERY_CODES = 2;

// What the section shows. The key, the URI and the recovery codes live only in
// the step that shows them, and leave with it.
export type Flow =
  | { step: "summary" }
  | { step: "scan"; enrolment: EnrolmentStart; replacing: boolean }
  | { step: "restart"; replacing: boolean; reason: "expired" | "attempts" }
  | { step: "codes"; codes: string[]; regenerated: boolean };

const SUMMARY: Flow = { step: "summary" };

const isProblem = (err: unknown, status: number, title: string): err is ApiError =>
  err instanceof ApiError && err.status === status && err.title === title;

// The stale-session refusal changed nothing on the server (#1280), and the API
// layer never resends it, so the user only needs to try again.
function stale(err: unknown): unknown {
  return isProblem(err, 401, "Auth.CredentialsSuperseded") ? new Error(i18n.t("account:sessionChanged")) : err;
}

export function useTwoFactor() {
  const { isAuthenticated, twoFactor: enrolled } = useAuth();
  const status = useLatestLoad([], getTwoFactorStatus);
  const dialog = useDialogAction(SCOPES);
  const { busy, run, openDialog, dismissDialog } = dialog;
  const [flow, setFlow] = useState<Flow>(SUMMARY);
  const [action, setAction] = useState<ProofAction | null>(null);
  const [proof, setProof] = useState<StepUpProof>(NO_PROOF);
  const [code, setCode] = useState("");
  const [message, setMessage] = useState<string | null>(null);
  const [codesSaved, setCodesSaved] = useState(false);

  // #1277 MC3 (maintainer, 2026-11-05: "Block both"): confirm and regenerate
  // hand out codes once, and unsaved codes exist only here, so the nav and
  // Back stay on the page until they are settled and saved.
  const confirming = dialog.isPending("confirm");
  useShellLock(confirming);
  useHistoryHold(confirming || dialog.isPending("regenerate") || (flow.step === "codes" && !codesSaved));

  // #308 — a proof, a code, a key or a recovery code never outlives the session.
  useEffect(() => {
    if (isAuthenticated) return;
    setProof(NO_PROOF);
    setCode("");
    setFlow(SUMMARY);
    setAction(null);
    for (const scope of SCOPES) dismissDialog(scope);
  }, [isAuthenticated, dismissDialog]);

  // Every way out clears the proof (cancel, send, logout), so opening does not.
  function openProof(next: ProofAction) {
    openDialog(next);
    setMessage(null);
    setAction(next);
  }

  function closeProof() {
    setProof(NO_PROOF);
    setAction(null);
    if (action) dismissDialog(action);
  }

  async function send(next: ProofAction, token: string, current: () => boolean) {
    if (next === "start") {
      // As the dialog does: replacing means the session proved two factors.
      const replacing = enrolled;
      const enrolment = await startTwoFactor(token);
      if (!current()) return;
      setCode("");
      openDialog("confirm");
      setFlow({ step: "scan", enrolment, replacing });
    } else if (next === "regenerate") {
      const { recoveryCodes } = await regenerateRecoveryCodes(token).catch((err: unknown) => {
        // The status was stale (#1277 contract item 8): reload it.
        if (!isProblem(err, 409, "Auth.TwoFactorNotEnabled")) throw err;
        status.retry();
        throw new Error(i18n.t("account:regenerateNotEnabled"));
      });
      status.retry();
      if (!current()) return;
      setCodesSaved(false);
      setFlow({ step: "codes", codes: recoveryCodes, regenerated: true });
    } else {
      await turnOffTwoFactor(token);
      status.retry();
      if (!current()) return;
      setMessage(i18n.t("account:turnedOffMessage"));
    }
    setAction(null);
    dismissDialog(next);
  }

  async function onProof(e: FormEvent) {
    e.preventDefault();
    const next = action;
    if (!next || busy) return;
    setMessage(null);
    await run(next, async (current) => {
      const entered = proof;
      setProof(NO_PROOF);
      const token = (await stepUp(entered)).token;
      if (!current()) return;
      await send(next, token, current).catch((err: unknown) => { throw stale(err); });
    }, { dialog: next });
  }

  async function onConfirm(e: FormEvent) {
    e.preventDefault();
    if (flow.step !== "scan" || busy) return;
    const { replacing } = flow;
    await run("confirm", async (current) => {
      const typed = code;
      setCode("");
      let recoveryCodes: string[] | undefined;
      try {
        ({ recoveryCodes } = await confirmTwoFactor(typed));
      } catch (err) {
        // The fifth wrong code ended the enrolment, and every later confirm
        // answers 409. A stale session's enrolment is bound to the old epoch,
        // so it can only answer 409 too. Each way the user starts again.
        const attemptsLeft = err instanceof ApiError ? err.extensions.attemptsLeft : undefined;
        const reason = isProblem(err, 409, "Auth.EnrolmentExpired") || isProblem(err, 401, "Auth.CredentialsSuperseded") ? "expired"
          : isProblem(err, 400, "Auth.EnrolmentCodeIncorrect") && attemptsLeft === 0 ? "attempts" : null;
        if (reason === null) throw incorrect(err, attemptsLeft);
        if (current()) setFlow({ step: "restart", replacing, reason });
        return;
      }
      status.retry();
      if (!current()) return;
      if (recoveryCodes) {
        setCodesSaved(false);
        setFlow({ step: "codes", codes: recoveryCodes, regenerated: false });
      } else {
        setFlow(SUMMARY);
        setMessage(i18n.t("account:replacedMessage"));
      }
    });
  }

  function leaveFlow() {
    dismissDialog("confirm");
    setCode("");
    setFlow(SUMMARY);
  }

  return {
    status, flow, action, proof, setProof, code, setCode, message, dialog, codesSaved, setCodesSaved,
    openProof, closeProof, onProof, onConfirm, leaveFlow,
  };
}

// A malformed code answers without a count (#1277 contract item 7).
function incorrect(err: unknown, attemptsLeft: unknown): unknown {
  if (!isProblem(err, 400, "Auth.EnrolmentCodeIncorrect")) return err;
  return new Error(typeof attemptsLeft === "number"
    ? i18n.t("account:codeIncorrectAttempts", { count: attemptsLeft })
    : i18n.t("account:codeIncorrect"));
}

export type TwoFactor = ReturnType<typeof useTwoFactor>;
