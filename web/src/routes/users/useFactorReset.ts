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
// decision 10). Each kind is its own dialog, run scope and idempotency key,
// and one record here says everything about it.
export type FactorReset = "reset-two-factor" | "remove-passkeys";

const name = (u: User) => u.displayName ?? u.email;

// Every text field is the translated words for the target user.
type Words = (u: User) => string;
export const FACTOR_RESETS: Record<FactorReset, {
  send: (id: string, key?: string, stepUpToken?: string) => Promise<void>;
  title: Words; warning: Words; note: Words | null; submit: Words; done: Words; notFound: Words;
}> = {
  "reset-two-factor": {
    send: resetUserTwoFactor,
    title: () => i18n.t("users:resetTwoFactorTitle"),
    warning: () => i18n.t("users:resetTwoFactorWarningBody"),
    note: null,
    submit: () => i18n.t("users:resetTwoFactorSubmitButton"),
    done: (u) => i18n.t("users:twoFactorResetMessage", { email: u.email }),
    notFound: () => i18n.t("users:resetTwoFactorNotFoundError"),
  },
  "remove-passkeys": {
    send: removeUserPasskeys,
    title: (u) => i18n.t("users:removePasskeysTitle", { name: name(u) }),
    warning: (u) => i18n.t("users:removePasskeysWarningBody", { count: u.passkeyCount }),
    note: () => i18n.t("users:removePasskeysNote"),
    submit: () => i18n.t("users:removePasskeysSubmitButton"),
    done: (u) => i18n.t("users:passkeysRemovedMessage", { email: u.email }),
    notFound: () => i18n.t("users:removePasskeysNotFoundError"),
  },
};

// Same shape as useDisableEnable: the flight guard, the message slots and the
// idempotency keys stay on the page (#703), as does the step-up proof the
// page clears on logout (#308). One proof serves every kind: only one of
// their dialogs is open at a time, and each open and close clears it.
export function useFactorReset({
  kind,
  dialog,
  setUsers,
  keyFor,
  clearKey,
  setMessage,
  proof,
  setProof,
}: {
  kind: FactorReset;
  dialog: DialogAction<FactorReset>;
  setUsers: Dispatch<SetStateAction<User[] | null>>;
  keyFor: (scope: string) => string;
  clearKey: (scope: string) => void;
  setMessage: Dispatch<SetStateAction<string | null>>;
  proof: StepUpProof;
  setProof: Dispatch<SetStateAction<StepUpProof>>;
}) {
  const { busy, run, openDialog, dismissDialog } = dialog;
  const [resetUser, setResetUser] = useState<User | null>(null);
  const config = FACTOR_RESETS[kind];

  function openReset(u: User) {
    openDialog(kind);
    setMessage(null);
    setProof(NO_PROOF);
    setResetUser(u);
  }

  function closeReset() {
    setProof(NO_PROOF);
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
      const enteredProof = proof;
      setProof(NO_PROOF);
      const token = (await stepUp(enteredProof)).token;
      if (!current()) return;

      await config.send(target.id, keyFor(scope), token).catch((err: unknown) => {
        // A 404 has no problem body for the errors catalog to key on.
        throw err instanceof ApiError && err.status === 404 ? new Error(config.notFound(target)) : err;
      });
      clearKey(scope);
      setUsers(await listUsers());
      if (!current()) return;
      setMessage(config.done(target));
      closeReset();
    }, { dialog: kind });
  }

  return { kind, resetUser, openReset, closeReset, onReset, proof, setProof };
}
