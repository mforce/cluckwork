import { useState } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { listUsers, resetUserTwoFactor } from "../../api/cluckwork";
import type { User } from "../../api/cluckwork";
import { ApiError, stepUp, NO_PROOF } from "../../api/client";
import type { StepUpProof } from "../../api/client";
import type { DialogAction } from "../../components/useDialogAction";
import i18n from "../../i18n";

// #1280 — an Owner resets another user's two-factor so they can enrol again.
// Same shape as useDisableEnable: the flight guard, the message slots and the
// idempotency keys stay on the page (#703), as does the step-up proof the
// page clears on logout (#308).
export function useResetTwoFactor({
  dialog,
  setUsers,
  keyFor,
  clearKey,
  setMessage,
  resetStepUpProof,
  setResetStepUpProof,
}: {
  dialog: DialogAction<"reset-two-factor">;
  setUsers: Dispatch<SetStateAction<User[] | null>>;
  keyFor: (scope: string) => string;
  clearKey: (scope: string) => void;
  setMessage: Dispatch<SetStateAction<string | null>>;
  resetStepUpProof: StepUpProof;
  setResetStepUpProof: Dispatch<SetStateAction<StepUpProof>>;
}) {
  const { busy, run, openDialog, dismissDialog } = dialog;
  const [resetUser, setResetUser] = useState<User | null>(null);

  function openReset(u: User) {
    openDialog("reset-two-factor");
    setMessage(null);
    setResetStepUpProof(NO_PROOF);
    setResetUser(u);
  }

  function closeReset() {
    setResetStepUpProof(NO_PROOF);
    setResetUser(null);
    dismissDialog("reset-two-factor");
  }

  async function onReset(e: FormEvent) {
    e.preventDefault();
    const target = resetUser;
    if (!target || busy) return;
    setMessage(null);
    const scope = `reset-two-factor:${target.id}`;
    await run(scope, async (current) => {
      const enteredProof = resetStepUpProof;
      setResetStepUpProof(NO_PROOF);
      const token = (await stepUp(enteredProof)).token;
      if (!current()) return;

      await resetUserTwoFactor(target.id, keyFor(scope), token).catch((err: unknown) => {
        // A 404 has no problem body for the errors catalog to key on.
        throw err instanceof ApiError && err.status === 404 ? new Error(i18n.t("users:resetTwoFactorNotFoundError")) : err;
      });
      clearKey(scope);
      setUsers(await listUsers());
      if (!current()) return;
      setMessage(i18n.t("users:twoFactorResetMessage", { email: target.email }));
      closeReset();
    }, { dialog: "reset-two-factor" });
  }

  return { resetUser, openReset, closeReset, onReset };
}
