import { useState } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { changeUserRole, listUsers } from "../../api/cluckwork";
import type { User } from "../../api/cluckwork";
import { stepUp, NO_PROOF } from "../../api/client";
import type { StepUpProof } from "../../api/client";
import type { DialogAction } from "../../components/useDialogAction";
import i18n from "../../i18n";
import { roleLabel } from "../../i18n/enums";

// The role-change dialog's state and handlers, moved out of UsersPage
// (#1215). The flight guard, the message slots and the idempotency keys stay
// on the page (#703) and are passed in, as is the step-up proof the page
// clears on logout (#308).
export function useChangeRole({
  dialog,
  setUsers,
  keyFor,
  clearKey,
  setMessage,
  roleStepUpProof,
  setRoleStepUpProof,
}: {
  dialog: DialogAction<"change-role">;
  setUsers: Dispatch<SetStateAction<User[] | null>>;
  keyFor: (scope: string) => string;
  clearKey: (scope: string) => void;
  setMessage: Dispatch<SetStateAction<string | null>>;
  roleStepUpProof: StepUpProof;
  setRoleStepUpProof: Dispatch<SetStateAction<StepUpProof>>;
}) {
  const { busy, run, openDialog, dismissDialog } = dialog;

  // #355 — promote/demote an existing user's role, own dialog for the same
  // reason as password reset above: a higher-consequence action than a name
  // edit, not one stray keystroke away from a typo.
  const [roleUser, setRoleUser] = useState<User | null>(null);
  const [roleValue, setRoleValue] = useState("Worker");

  // #355 — open/close the role dialog for a user, seeded with their current role.
  function openRole(u: User) {
    openDialog("change-role");
    setMessage(null);
    setRoleValue(u.role);
    setRoleStepUpProof(NO_PROOF);
    setRoleUser(u);
  }

  function closeRole() {
    setRoleStepUpProof(NO_PROOF); // #308 — never leave a typed proof behind
    setRoleUser(null);
    dismissDialog("change-role");
  }

  async function onChangeRole(e: FormEvent) {
    e.preventDefault();
    const target = roleUser;
    if (!target || busy) return;
    setMessage(null);
    const keyScope = `role:${target.id}`;
    await run(`change-role:${target.id}`, async (current) => {
      // #355/#360 — every role mutation changes durable authorization. Read
      // then clear before awaiting issuance; the grant is spent once below.
      const enteredProof = roleStepUpProof;
      setRoleStepUpProof(NO_PROOF);
      const stepUpToken = (await stepUp(enteredProof)).token;
      if (!current()) return;

      await changeUserRole(target.id, { role: roleValue }, keyFor(keyScope), stepUpToken);
      clearKey(keyScope); // write confirmed before any refresh (#163 review)
      setUsers(await listUsers());
      if (!current()) return;
      setMessage(i18n.t("users:roleChangedMessage", { email: target.email, role: roleLabel(roleValue) }));
      closeRole();
    }, { dialog: "change-role" });
  }


  return { roleUser, roleValue, setRoleValue, openRole, closeRole, onChangeRole };
}
