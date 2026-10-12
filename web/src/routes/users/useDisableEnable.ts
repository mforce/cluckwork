import { useState } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { disableUser, enableUser, listUsers } from "../../api/cluckwork";
import type { User } from "../../api/cluckwork";
import { stepUp } from "../../api/client";
import type { DialogAction } from "../../components/useDialogAction";
import i18n from "../../i18n";

// The shared disable/enable dialog's state and handlers, moved out of
// UsersPage (#1215). The flight guard, the message slots and the idempotency
// keys stay on the page (#703) and are passed in, as is the proof password
// the page clears on logout (#308).
export function useDisableEnable({
  dialog,
  setUsers,
  keyFor,
  clearKey,
  setMessage,
  stepUpPassword,
  setStepUpPassword,
}: {
  dialog: DialogAction<"disable-enable">;
  setUsers: Dispatch<SetStateAction<User[] | null>>;
  keyFor: (scope: string) => string;
  clearKey: (scope: string) => void;
  setMessage: Dispatch<SetStateAction<string | null>>;
  stepUpPassword: string;
  setStepUpPassword: Dispatch<SetStateAction<string>>;
}) {
  const { busy, run, openDialog, dismissDialog } = dialog;

  // #356 — disable/enable a user, both behind ONE dialog that is itself the
  // confirmation: a destructive warning body, an OPTIONAL reason (disable
  // only — the API's DisableUserCommand.Reason is nullable, capped at 200
  // chars, and the product call was explicit that a mandatory reason just
  // gets typed "x"), and the step-up password DisableUser/EnableUser always
  // require (#308) regardless of the target's role. "mode" picks which
  // endpoint + copy applies.
  const [stepUpUser, setStepUpUser] = useState<User | null>(null);
  const [stepUpMode, setStepUpMode] = useState<"disable" | "enable" | null>(null);

  const [disableReason, setDisableReason] = useState("");

  // #356 — open/close the shared disable/enable dialog. The Disable and Enable
  // row buttons call this identically; "mode" is the only difference. The
  // reason is not a parameter: the dialog collects it itself, seeded blank,
  // through a controlled textarea.
  function openStepUp(u: User, mode: "disable" | "enable") {
    // Every open ends the session on screen (#703) — a MODE change included:
    // both modes share one error scope (one dialog, one title swap), and the
    // row's Disable/Enable button flips with u.disabledAt, so a stale
    // "Cannot disable the sole remaining owner" from a failed disable attempt
    // must not still be showing when this reopens in enable mode for the
    // same user (local review of #492's merge-driven conversion to
    // useDialogErrors) — a message about the wrong operation entirely.
    openDialog("disable-enable");
    setMessage(null);
    setStepUpPassword("");
    setDisableReason("");
    setStepUpMode(mode);
    setStepUpUser(u);
  }

  function closeStepUp() {
    setStepUpPassword(""); // #308 — never leave a typed proof password behind
    setDisableReason("");
    setStepUpMode(null);
    setStepUpUser(null);
    dismissDialog("disable-enable");
  }

  async function onSubmitStepUp(e: FormEvent) {
    e.preventDefault();
    const target = stepUpUser;
    const mode = stepUpMode;
    if (!target || !mode || busy) return;
    setMessage(null);
    const scope = `${mode}:${target.id}`;
    await run(scope, async (current) => {
      // #308 — read-then-clear-before-await, same pattern as every other
      // step-up site on this screen: the proof password never sits in state
      // across the network call that consumes it.
      const enteredPassword = stepUpPassword;
      setStepUpPassword("");

      // #356 — grouped here with the password for readability, NOT because
      // reading it after the await would be a bug. An earlier revision of
      // this comment claimed exactly that ("defence in depth" against a
      // late read filing one dialog's reason against another user), and a
      // review probe disproved it: `disableReason` is a `const` this
      // closure captured at THIS render, not a live ref. Retyping the
      // textarea triggers a new render with a new `onSubmitStepUp` closure
      // over a new `disableReason` binding — it cannot reach back and
      // mutate the one this already-running invocation holds. So a read
      // before or after the await, within one invocation, is provably the
      // same value; unlike the password above, there is no state-exposure
      // reason to move it either, since a reason is not a secret.
      //
      // Reason is optional: empty or whitespace sends null, never "".
      const enteredReason = disableReason.trim() || null;
      const token = (await stepUp(enteredPassword)).token;
      // Dismissed while the grant was being issued: like every other step-up
      // write on this screen, the write is refused rather than made — the
      // grant is dropped unspent (#703 PR 4 review; pinned by `does not let a
      // dismissed disable continuation write`).
      if (!current()) return;

      if (mode === "disable") {
        await disableUser(target.id, { reason: enteredReason }, keyFor(scope), token);
      } else {
        await enableUser(target.id, keyFor(scope), token);
      }
      clearKey(scope); // write confirmed before any refresh (#163 review)
      setUsers(await listUsers());
      // Dismissed, or reopened for another user, the same user, or the other
      // MODE, while this was in flight. The old guard compared user ids, so a
      // same-user reopen — a disable followed by an enable of the same person
      // included — passed and this stale success closed the dialog the user
      // had just reopened (#703); the session generation tells them apart.
      // A mid-flight REOPEN is not reachable through the UI (both row
      // triggers are disabled for the whole flight — pinned by `keeps both
      // row triggers disabled…`); a mid-flight DISMISSAL is, pinned by
      // `supersedes the disable on dismissal alone…`; the close itself by
      // `a successful disable closes its dialog…`.
      if (!current()) return;
      setMessage(i18n.t(mode === "disable" ? "users:userDisabledMessage" : "users:userEnabledMessage",
        { email: target.email }));
      closeStepUp();
    }, { dialog: "disable-enable" });
  }


  return { stepUpUser, stepUpMode, disableReason, setDisableReason, openStepUp, closeStepUp, onSubmitStepUp };
}
