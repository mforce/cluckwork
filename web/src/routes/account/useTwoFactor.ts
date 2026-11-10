import { useCallback, useEffect, useState } from "react";
import type { FormEvent } from "react";
import { getTwoFactorStatus, regenerateRecoveryCodes, startTwoFactor } from "../../api/cluckwork";
import type { EnrolmentStart } from "../../api/cluckwork";
import { ApiError, confirmTwoFactor, NO_PROOF, stepUp, turnOffTwoFactor } from "../../api/client";
import type { StepUpProof } from "../../api/client";
import { useAuth } from "../../auth/useAuth";
import { useDialogAction } from "../../components/useDialogAction";
import { useLatestLoad } from "../../components/useLatestLoad";
import i18n from "../../i18n";
import { newId } from "../../lib/ids";
import { useMe } from "../../session/SessionContext";
import { useRecoveryCodesSlot } from "./recoveryCodes";
import type { Delivery } from "./recoveryCodes";

// #1277 — the Security section's two-factor authentication. Start,
// Regenerate and Turn off each step up in a dialog first; confirm is the
// enrolment screen's own form.
export type ProofAction = "start" | "regenerate" | "turn-off";
const SCOPES = ["start", "regenerate", "turn-off", "confirm"] as const;

// From this many recovery codes left, the section warns (#1274 decision 9).
export const LOW_RECOVERY_CODES = 2;

// What the section shows. The key and the URI live only in the step that
// shows them, and leave with it. New recovery codes go to the slot above the
// routes (recoveryCodes.tsx), never into this state.
export type Flow =
  | { step: "summary" }
  | { step: "scan"; enrolment: EnrolmentStart; replacing: boolean }
  | { step: "restart"; replacing: boolean; reason: "expired" | "attempts" | "session" };

const SUMMARY: Flow = { step: "summary" };

// #1281 — the deadline banner's Set up opens the setup dialog on Account. One
// shot, as AuditPage's pivot focus is: a reload or Back does not open it again.
let pendingSetUp: string | null = null;
export function requestSetUp(): { setUpTwoFactor: string } {
  pendingSetUp = newId();
  return { setUpTwoFactor: pendingSetUp };
}
export function takeSetUpRequest(state: unknown): boolean {
  const id = (state as { setUpTwoFactor?: unknown } | null)?.setUpTwoFactor;
  if (id === undefined || id !== pendingSetUp) return false;
  pendingSetUp = null;
  return true;
}

const isProblem = (err: unknown, status: number, title: string): err is ApiError =>
  err instanceof ApiError && err.status === status && err.title === title;

// The stale-session refusal changed nothing on the server (#1280), and the API
// layer never resends it, so the user only needs to try again.
function stale(err: unknown): unknown {
  return isProblem(err, 401, "Auth.CredentialsSuperseded") ? new Error(i18n.t("account:sessionChanged")) : err;
}

export function useTwoFactor() {
  // #1281 — credentialKey: a new user or new credentials (a role change)
  // can mean the gate no longer refuses this user, so the status is read
  // again. A refresh keeps it, so a refresh alone reads nothing.
  const { isAuthenticated, twoFactor: enrolled, credentialKey } = useAuth();
  const dialog = useDialogAction(SCOPES);
  const { busy, run, openDialog, dismissDialog } = dialog;
  const [flow, setFlow] = useState<Flow>(SUMMARY);
  const [action, setAction] = useState<ProofAction | null>(null);
  const [proof, setProof] = useState<StepUpProof>(NO_PROOF);
  const [code, setCode] = useState("");
  const [message, setMessage] = useState<string | null>(null);
  const slot = useRecoveryCodesSlot();
  // #1277 — reloaded each time a code-issuing request settles, and
  // whenever the session's two_factor claim changes (a Turn off that
  // answers after the user came back), so a page mounted since still shows
  // what the server now holds. Both triggers stay: a Turn off is not a slot
  // request, and a regenerate changes no claim.
  const status = useLatestLoad([slot.settled, enrolled, credentialKey], getTwoFactorStatus);
  // #1281 — counts returns to the summary with no dialog to take focus (a
  // Cancel, or a Replace that issues no codes), so the screen can move focus
  // to where the user goes back to.
  const [backToSummary, setBackToSummary] = useState(0);
  const email = useMe()?.email ?? "";

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
  const openProof = useCallback((next: ProofAction) => {
    openDialog(next);
    setMessage(null);
    setAction(next);
  }, [openDialog]);

  function closeProof() {
    setProof(NO_PROOF);
    setAction(null);
    if (action) dismissDialog(action);
  }

  async function send(next: "start" | "turn-off", token: string, current: () => boolean) {
    if (next === "start") {
      // As the dialog does: replacing means the session proved two factors.
      const replacing = enrolled;
      const enrolment = await startTwoFactor(token);
      if (!current()) return;
      setCode("");
      openDialog("confirm");
      setFlow({ step: "scan", enrolment, replacing });
    } else {
      await turnOffTwoFactor(token);
      // A 204 (two-factor was already off) rotates nothing, so neither the
      // claim nor a settle would reload the status; this does.
      status.retry();
      if (!current()) return;
      setMessage(i18n.t("account:turnedOffMessage"));
    }
    setAction(null);
    dismissDialog(next);
  }

  // dialogCurrent is the Regenerate dialog's session; delivery.live() is the
  // slot's request in this tab's session. Either can end without the other.
  async function regenerate(token: string, dialogCurrent: () => boolean, delivery: Delivery) {
    // Either refusal reloads the status through the slot: release ends the
    // request and the status reads again.
    const { recoveryCodes } = await regenerateRecoveryCodes(token).catch((err: unknown) => {
      // The status was stale (#1277 contract item 8).
      if (!isProblem(err, 409, "Auth.TwoFactorNotEnabled")) throw err;
      throw new Error(i18n.t("account:regenerateNotEnabled"));
    });
    // The old set no longer works, so a broken answer is said, not shown.
    if (!isCodeSet(recoveryCodes)) throw new Error(i18n.t("account:codesMissing"));
    // Before the dialogCurrent() check: the codes are shown even if the user
    // has left this page, because the old set no longer works.
    delivery.deliver(recoveryCodes, { regenerated: true, email });
    if (!dialogCurrent()) return;
    setAction(null);
    dismissDialog("regenerate");
  }

  // The step-up proof every Security dialog sends first.
  async function prove() {
    const entered = proof;
    setProof(NO_PROOF);
    return (await stepUp(entered)).token;
  }

  async function onProof(e: FormEvent) {
    e.preventDefault();
    const next = action;
    if (!next || busy) return;
    setMessage(null);
    await run(next, async (current) => {
      if (next !== "regenerate") {
        const token = await prove();
        if (!current()) return;
        await send(next, token, current).catch((err: unknown) => { throw stale(err); });
        return;
      }
      // Taken before the step-up proof, so the slot reads the session then,
      // and released however the request ends.
      const delivery = slot.expect();
      if (delivery === null) return;
      try {
        const token = await prove();
        // #1277 — a session that ended during the proof sends nothing: the
        // server would replace the set, and deliver would drop the only copy.
        if (!current() || !delivery.live()) return;
        await regenerate(token, current, delivery).catch((err: unknown) => { throw stale(err); });
      } finally {
        delivery.release();
      }
    }, { dialog: next });
  }

  async function onConfirm(e: FormEvent) {
    e.preventDefault();
    if (flow.step !== "scan" || busy) return;
    const { replacing } = flow;
    await run("confirm", async (current) => {
      const delivery = slot.expect();
      if (delivery === null) return;
      try {
        await confirmWith(delivery, current, replacing);
      } finally {
        delivery.release();
      }
    });
  }

  async function confirmWith(delivery: Delivery, current: () => boolean, replacing: boolean) {
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
      const reason = isProblem(err, 409, "Auth.EnrolmentExpired") ? "expired"
        : isProblem(err, 401, "Auth.CredentialsSuperseded") ? "session"
        : isProblem(err, 400, "Auth.EnrolmentCodeIncorrect") && attemptsLeft === 0 ? "attempts" : null;
      if (reason === null) throw incorrect(err, attemptsLeft);
      if (current()) setFlow({ step: "restart", replacing, reason });
      return;
    }
    // A first enrolment's answer carries the codes; without them the user is
    // enrolled with none to save, and Regenerate is the way out.
    if (!replacing && !isCodeSet(recoveryCodes)) throw new Error(i18n.t("account:codesMissing"));
    // Before the current() check, as regenerate does: the user is enrolled
    // now, so the codes are shown wherever the user has gone.
    if (isCodeSet(recoveryCodes)) delivery.deliver(recoveryCodes, { regenerated: false, email });
    if (!current()) return;
    setFlow(SUMMARY);
    if (isCodeSet(recoveryCodes)) return;
    setMessage(i18n.t("account:replacedMessage"));
    setBackToSummary((n) => n + 1);
  }

  function leaveFlow() {
    dismissDialog("confirm");
    setCode("");
    setFlow(SUMMARY);
    setBackToSummary((n) => n + 1);
  }

  return {
    status, flow, action, proof, setProof, code, setCode, message, dialog,
    openProof, closeProof, onProof, onConfirm, leaveFlow, backToSummary,
  };
}

// An answer's codes, if it has any: a missing or empty list is no set.
const isCodeSet = (codes: string[] | undefined): codes is string[] => Array.isArray(codes) && codes.length > 0;

// A malformed code answers without a count (#1277 contract item 7).
function incorrect(err: unknown, attemptsLeft: unknown): unknown {
  if (!isProblem(err, 400, "Auth.EnrolmentCodeIncorrect")) return err;
  return new Error(typeof attemptsLeft === "number"
    ? i18n.t("account:codeIncorrectAttempts", { count: attemptsLeft })
    : i18n.t("account:codeIncorrect"));
}

export type TwoFactor = ReturnType<typeof useTwoFactor>;
