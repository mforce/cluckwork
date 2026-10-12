import { useState } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { setUserPassword } from "../../api/cluckwork";
import type { User } from "../../api/cluckwork";
import { stepUp } from "../../api/client";
import type { DialogAction } from "../../components/useDialogAction";
import i18n from "../../i18n";

// The password-reset dialog's state and handlers, moved out of UsersPage
// (#1215). The flight guard, the message slots and the idempotency keys stay
// on the page (#703) and are passed in, as is the proof password the page
// clears on logout (#308).
export function useSetPassword({
  dialog,
  keyFor,
  clearKey,
  setMessage,
  pwStepUpPassword,
  setPwStepUpPassword,
}: {
  dialog: DialogAction<"set-password">;
  keyFor: (scope: string) => string;
  clearKey: (scope: string) => void;
  setMessage: Dispatch<SetStateAction<string | null>>;
  pwStepUpPassword: string;
  setPwStepUpPassword: Dispatch<SetStateAction<string>>;
}) {
  const { busy, errors, run, openDialog, dismissDialog } = dialog;

  // #165 password reset: kept in its own dialog rather than folded into the name
  // edit — setting someone's password is a different, higher-consequence action
  // and shouldn't be one stray keystroke away from a typo fix.
  const [pwUser, setPwUser] = useState<User | null>(null);
  const [pwValue, setPwValue] = useState("");
  const [pwConfirm, setPwConfirm] = useState("");

  // #165 — open/close the password dialog for a user.
  function openPassword(u: User) {
    openDialog("set-password");
    setMessage(null);
    setPwValue("");
    setPwConfirm("");
    setPwStepUpPassword("");
    setPwUser(u);
  }

  function closePassword() {
    // Don't leave the typed plaintext sitting in component state after the
    // dialog is gone (#165 review; #308 for the step-up field).
    setPwValue("");
    setPwConfirm("");
    setPwStepUpPassword("");
    setPwUser(null);
    dismissDialog("set-password");
  }

  async function onSetPassword(e: FormEvent) {
    e.preventDefault();
    const target = pwUser;
    // The mismatch check stays OUTSIDE the flight (it is validation, not
    // work), so it keeps the old busy guard alongside the hook's — and that
    // guard is load-bearing for the un-mute below: without it a second submit
    // while an abandoned attempt is still out would un-mute that attempt's
    // failure into this dialog (#703 PR 4 review).
    if (!target || busy) return;
    // Before the validation below, not only inside run(): a mismatch never
    // reaches run(), so without this the slot would still hold the previous
    // attempt's verdict — and a mute left by a dismissal would swallow this.
    errors.beginAttempt("set-password");
    setMessage(null);
    if (pwValue !== pwConfirm) {
      errors.report("set-password", i18n.t("users:passwordMismatchMessage"));
      return;
    }
    const keyScope = `password:${target.id}`;
    await run(`set-password:${target.id}`, async (current) => {
      // #308/#360 — every administrative reset replaces an authenticator,
      // regardless of target role. Read then clear before awaiting issuance.
      const enteredPassword = pwStepUpPassword;
      setPwStepUpPassword("");
      const stepUpToken = (await stepUp(enteredPassword)).token;
      if (!current()) return;

      await setUserPassword(target.id, { newPassword: pwValue }, keyFor(keyScope), stepUpToken);
      clearKey(keyScope); // write confirmed before any refresh (#163 review)
      if (!current()) return;
      setMessage(i18n.t("users:passwordSetMessage", { email: target.email }));
      closePassword();
    }, { dialog: "set-password" });
  }


  return { pwUser, pwValue, setPwValue, pwConfirm, setPwConfirm, openPassword, closePassword, onSetPassword };
}
