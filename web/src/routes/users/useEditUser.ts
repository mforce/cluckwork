import { useState } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { listUsers, updateUser } from "../../api/cluckwork";
import type { User } from "../../api/cluckwork";
import type { DialogAction } from "../../components/useDialogAction";
import i18n from "../../i18n";

// The edit-user dialog's state and handlers, moved out of UsersPage (#1215):
// the display-name edit seeded from the row, its close edge and the update.
// The flight guard, the message slots and the idempotency keys stay on the
// page (#703) and are passed in.
export function useEditUser({
  dialog,
  setUsers,
  keyFor,
  clearKey,
  setMessage,
}: {
  dialog: DialogAction<"edit-user">;
  setUsers: Dispatch<SetStateAction<User[] | null>>;
  keyFor: (scope: string) => string;
  clearKey: (scope: string) => void;
  setMessage: Dispatch<SetStateAction<string | null>>;
}) {
  const { run, openDialog, dismissDialog } = dialog;

  // #163 edit: the user whose name is being edited, and the working value.
  const [editUser, setEditUser] = useState<User | null>(null);
  const [editName, setEditName] = useState("");

  // #163 — open the edit dialog seeded with the user's current name.
  // Every open handler below ends the session on screen (#703), the same
  // user's included: each open reseeds its form, so an attempt still out
  // belongs to a session that is over — its failure lands nowhere and its
  // success cannot close or reset the form the user is looking at now — and
  // a displaced user's verdict is dropped rather than rendered under the new
  // user's email in the title. Everything behind the topmost dialog is inert
  // (`Dialog.tsx`, #480), so a displacement reaches an open handler only
  // after a dismissal; both edges end the session all the same.
  function openEdit(u: User) {
    openDialog("edit-user");
    setMessage(null);
    setEditName(u.displayName ?? "");
    setEditUser(u);
  }

  function closeEdit() {
    setEditUser(null);
    dismissDialog("edit-user");
  }

  async function onUpdate(e: FormEvent) {
    e.preventDefault();
    const target = editUser;
    if (!target) return;
    const scope = `update:${target.id}`;
    await run(scope, async (current) => {
      setMessage(null);
      // Blank clears the name back to "—" (null); the server normalizes too.
      await updateUser(target.id, { name: editName.trim() || null }, keyFor(scope));
      // Clear the key once the WRITE is confirmed (before the refresh), so a
      // follow-up edit isn't replayed against this cached response (#163 review).
      clearKey(scope);
      await listUsers().then(setUsers);
      // The dialog may have been dismissed, or reopened — for another user OR
      // the same one — while this was in flight. The old guard compared user
      // ids, so a same-user reopen passed and this stale success closed the
      // dialog the user had just reopened (#703); the session generation
      // tells the two apart.
      if (!current()) return;
      setMessage(i18n.t("users:updatedMessage", { email: target.email }));
      closeEdit();
    }, { dialog: "edit-user" });
  }


  return { editUser, editName, setEditName, openEdit, closeEdit, onUpdate };
}
