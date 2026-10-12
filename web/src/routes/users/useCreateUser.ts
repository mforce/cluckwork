import { useState } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { createUser, listUsers } from "../../api/cluckwork";
import type { User } from "../../api/cluckwork";
import { stepUp } from "../../api/client";
import type { DialogAction } from "../../components/useDialogAction";
import i18n from "../../i18n";
import { roleLabel } from "../../i18n/enums";

// The create-user dialog's state and handlers, moved out of UsersPage
// (#1215): the form state, the open/close edges and the step-up-guarded
// create. The flight guard, the message slots and the idempotency keys stay
// on the page (#703) and are passed in, as is the proof password the page
// clears on logout (#308).
export function useCreateUser({
  dialog,
  setUsers,
  keyFor,
  clearKey,
  setMessage,
  createStepUpPassword,
  setCreateStepUpPassword,
}: {
  dialog: DialogAction<"create">;
  setUsers: Dispatch<SetStateAction<User[] | null>>;
  keyFor: (scope: string) => string;
  clearKey: (scope: string) => void;
  setMessage: Dispatch<SetStateAction<string | null>>;
  createStepUpPassword: string;
  setCreateStepUpPassword: Dispatch<SetStateAction<string>>;
}) {
  const { run, openDialog, dismissDialog } = dialog;

  const [creating, setCreating] = useState(false); // F131: create moved into a dialog
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState("Worker");
  const [name, setName] = useState(""); // #163 optional display name at creation

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    // The hook clears this dialog's slot, claims its session and reports a
    // failure into it (#703); `current()` says whether the session that
    // started this attempt is still the one on screen.
    await run("create", async (current) => {
      setMessage(null);
      // #308/#360 — every interactive creation establishes a durable login,
      // regardless of role. Read then clear the Owner's current password
      // before awaiting issuance; the grant is spent on this write only.
      const enteredPassword = createStepUpPassword;
      setCreateStepUpPassword("");
      const stepUpToken = (await stepUp(enteredPassword)).token;
      if (!current()) return;

      const scope = `create:${email.trim().toLowerCase()}`;
      await createUser(
        { email: email.trim(), password, role, name: name.trim() || undefined },
        keyFor(scope), stepUpToken);
      // Clear the key the instant the WRITE is confirmed — before the refresh —
      // so a later edit of the just-created user (a changed payload) can't replay
      // this cached response if the refresh below fails (#163 review).
      clearKey(scope);
      setUsers(await listUsers());
      // Superseded: the user exists and the list shows them, but the message
      // and the close belong to the session on screen now (#703).
      if (!current()) return;
      setMessage(i18n.t("users:createSuccessMessage", { role: roleLabel(role), email: email.trim() }));
      // #336 review — close through closeCreate() rather than repeating the
      // field resets here. The duplicated list had already drifted: it never
      // cleared createStepUpPassword, so a successful create could leave the
      // operator's own account password in state, visible on the next reopen.
      // One reset path means new dialog state can only be forgotten in one
      // place, not two — the #314 lesson, relearned.
      closeCreate();
    });
  }

  function openCreate() {
    setMessage(null);
    openDialog("create");
    setCreating(true);
  }

  // #314 — close the create dialog from any path (Cancel, X, Escape, overlay).
  // Don't leave the typed plaintext password sitting in component state after
  // the dialog is gone (same pattern as closePassword's #165 fix). Role resets
  // too: a stale "Admin" from an abandoned attempt would otherwise still be
  // selected on reopen, so an operator who thinks they're starting fresh can
  // grant admin by accident. Matches the full reset onCreate does on success.
  function closeCreate() {
    setCreating(false);
    setEmail("");
    setPassword("");
    setRole("Worker");
    setName("");
    setCreateStepUpPassword(""); // #308 — never leave a typed proof password behind
    dismissDialog("create");
  }


  return {
    creating,
    email, setEmail,
    password, setPassword,
    role, setRole,
    name, setName,
    onCreate,
    openCreate,
    closeCreate,
  };
}
