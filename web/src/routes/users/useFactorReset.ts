import { useState } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { listUsers, removeUserPasskeys, resetUserTwoFactor } from "../../api/cluckwork";
import type { User } from "../../api/cluckwork";
import { ApiError, stepUp, NO_PROOF } from "../../api/client";
import type { StepUpProof } from "../../api/client";
import type { DialogAction } from "../../components/useDialogAction";
import i18n from "../../i18n";

// #1280 — an Owner resets another user's two-factor so they can enrol again.
// #1284 — and, as a separate action, removes their passkeys (record 1282
// decision 10). Each kind is its own dialog, run scope and idempotency key.
export type FactorReset = "reset-two-factor" | "remove-passkeys";

const RESETS = {
  "reset-two-factor": { send: resetUserTwoFactor, done: "users:twoFactorResetMessage" },
  "remove-passkeys": { send: removeUserPasskeys, done: "users:passkeysRemovedMessage" },
} as const;

// Same shape as useDisableEnable: the flight guard, the message slots and the
// idempotency keys stay on the page (#703), as does the step-up proof the
// page clears on logout (#308).
export function useFactorReset({
  kind,
  dialog,
  setUsers,
  keyFor,
  clearKey,
  setMessage,
  resetStepUpProof,
  setResetStepUpProof,
}: {
  kind: FactorReset;
  dialog: DialogAction<FactorReset>;
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
    openDialog(kind);
    setMessage(null);
    setResetStepUpProof(NO_PROOF);
    setResetUser(u);
  }

  function closeReset() {
    setResetStepUpProof(NO_PROOF);
    setResetUser(null);
    dismissDialog(kind);
  }

  async function onReset(e: FormEvent) {
    e.preventDefault();
    const target = resetUser;
    if (!target || busy) return;
    setMessage(null);
    const scope = `${kind}:${target.id}`;
    await run(scope, async (current) => {
      const enteredProof = resetStepUpProof;
      setResetStepUpProof(NO_PROOF);
      const token = (await stepUp(enteredProof)).token;
      if (!current()) return;

      await RESETS[kind].send(target.id, keyFor(scope), token).catch((err: unknown) => {
        // A 404 has no problem body for the errors catalog to key on.
        throw err instanceof ApiError && err.status === 404 ? new Error(i18n.t("users:resetTwoFactorNotFoundError")) : err;
      });
      clearKey(scope);
      setUsers(await listUsers());
      if (!current()) return;
      setMessage(i18n.t(RESETS[kind].done, { email: target.email }));
      closeReset();
    }, { dialog: kind });
  }

  return { kind, resetUser, openReset, closeReset, onReset };
}
