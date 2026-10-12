import type { Dispatch, FormEvent, SetStateAction } from "react";
import { changeUserEmail, listUsers } from "../../api/cluckwork";
import type { User } from "../../api/cluckwork";
import { ApiError, stepUp } from "../../api/client";
import type { DialogAction } from "../../components/useDialogAction";
import { errText } from "../../lib/errText";
import i18n from "../../i18n";

// The change-email dialog's handlers, moved out of UsersPage (#1215). The
// dialog's state stays on the page — its logout effect (#308) clears it and
// ends the session — and the flight guard, the message slots and the
// idempotency keys are passed in (#703).
export function useChangeEmail({
  dialog,
  setUsers,
  keyFor,
  clearKey,
  setMessage,
  myId,
  emailUser,
  setEmailUser,
  emailValue,
  setEmailValue,
  emailStepUpPassword,
  setEmailStepUpPassword,
  setEmailFieldError,
}: {
  dialog: DialogAction<"change-email">;
  setUsers: Dispatch<SetStateAction<User[] | null>>;
  keyFor: (scope: string) => string;
  clearKey: (scope: string) => void;
  setMessage: Dispatch<SetStateAction<string | null>>;
  myId: string | null;
  emailUser: User | null;
  setEmailUser: Dispatch<SetStateAction<User | null>>;
  emailValue: string;
  setEmailValue: Dispatch<SetStateAction<string>>;
  emailStepUpPassword: string;
  setEmailStepUpPassword: Dispatch<SetStateAction<string>>;
  setEmailFieldError: Dispatch<SetStateAction<string | null>>;
}) {
  const { busy, errors, run, openDialog, dismissDialog } = dialog;

  function openEmail(u: User) {
    // A displaced user's key is spent with their session; the same user's is
    // kept, so a retry of an unchanged email replays rather than re-issues.
    if (emailUser !== null && emailUser.id !== u.id) clearKey(`change-email:${emailUser.id}`);
    openDialog("change-email");
    setMessage(null);
    setEmailValue(u.email);
    setEmailStepUpPassword("");
    setEmailFieldError(null);
    setEmailUser(u);
  }

  function closeEmail() {
    if (emailUser !== null) clearKey(`change-email:${emailUser.id}`);
    setEmailValue("");
    setEmailStepUpPassword("");
    setEmailFieldError(null);
    setEmailUser(null);
    dismissDialog("change-email");
  }

  async function onChangeEmail(e: FormEvent) {
    e.preventDefault();
    const target = emailUser;
    if (!target || busy) return;
    const targetId = target.id;
    const scope = `change-email:${targetId}`;
    setEmailFieldError(null);
    setMessage(null);
    await run(scope, async (current) => {
      // This dialog maps three outcomes itself, so it keeps its own catch and
      // gates every branch on `current()` — a superseded attempt lands nowhere,
      // exactly as the hook's own report would. The order below (gate before
      // the key clears and the list refreshes) is this dialog's own and is
      // kept as is (#703 PR 4, owner decision 2026-09-07).
      try {
        const password = emailStepUpPassword;
        setEmailStepUpPassword("");
        const grant = await stepUp(password);
        if (!current()) return;
        const trimmedEmail = emailValue.trim();
        await changeUserEmail(targetId, { email: trimmedEmail }, keyFor(scope), grant.token);
        if (!current()) return;
        clearKey(scope);
        const fresh = await listUsers();
        if (!current()) return;
        setUsers(fresh);
        setMessage(i18n.t("users:emailChangedMessage", { email: trimmedEmail }));
        closeEmail();
      } catch (err) {
        if (!current()) return;
        if (targetId === myId && err instanceof ApiError
          && err.status === 401 && err.title === "Auth.CredentialsSuperseded") {
          return;
        } else if (err instanceof ApiError && err.status === 409 && err.title === "Users.DuplicateEmail") {
          setEmailFieldError(i18n.t("users:duplicateEmailMessage"));
        } else if (err instanceof ApiError && err.title === "Users.LastOwner") {
          errors.report("change-email", i18n.t("users:lastOwnerEmailMessage"));
        } else {
          errors.report("change-email", errText(err));
        }
      }
    }, { dialog: "change-email" });
  }


  return { openEmail, closeEmail, onChangeEmail };
}
