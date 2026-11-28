import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { listPasskeys, registerPasskey, removePasskey, renamePasskey, startPasskeyRegistration } from "../../api/cluckwork";
import { createPasskey, passkeysSupported } from "../../auth/passkey";
import type { Passkey } from "../../api/cluckwork";
import { useAuth } from "../../auth/useAuth";
import { useDialogAction } from "../../components/useDialogAction";
import { useLatestLoad } from "../../components/useLatestLoad";
import i18n from "../../i18n";
import { useStepUpDialog } from "./useStepUpDialog";

// #1284 — the Security section's passkeys (record 1282). Add steps up first,
// then names the passkey and runs the browser's ceremony (#1283
// "Registration A"); Remove steps up in its dialog; Rename needs no step-up.
const SCOPES = ["add", "remove", "rename", "create"] as const;

// The create step after the step-up. Its grant is spent by the finish
// whatever it answers, so a sent finish leaves the step "spent", and the next
// try steps up again; a browser cancel sends nothing and keeps the grant.
type CreatePhase = { phase: "ready" | "cancelled"; grant: string } | { phase: "spent" };
export type CreateStep = { step: "create"; first: boolean } & CreatePhase;

export type PasskeyFlow =
  | { step: "list" }
  | CreateStep
  | { step: "created"; name: string }
  | { step: "unsupported" };

const LIST: PasskeyFlow = { step: "list" };

// The browser's refusals, as the create step says them. NotAllowedError, a
// cancel or a timeout, is not one: the step shows it as a cancel.
function browserError(err: unknown): Error {
  const exists = err instanceof DOMException && err.name === "InvalidStateError";
  return new Error(i18n.t(exists ? "account:passkeyExists" : "account:passkeyFailed"));
}

export function usePasskeys() {
  const { isAuthenticated, twoFactor: enrolled, credentialKey } = useAuth();
  const dialog = useDialogAction(SCOPES);
  const { busy, run, openDialog, dismissDialog } = dialog;
  const stepUp = useStepUpDialog<"add" | "remove">(dialog);
  const list = useLatestLoad([credentialKey], listPasskeys);
  const [flow, setFlow] = useState<PasskeyFlow>(LIST);
  const [removing, setRemoving] = useState<Passkey | null>(null);
  const [renaming, setRenaming] = useState<Passkey | null>(null);
  const [newName, setNewName] = useState("");
  const [createName, setCreateName] = useState("");
  const [message, setMessage] = useState<string | null>(null);

  // #308 — a grant never outlives the session.
  useEffect(() => {
    if (isAuthenticated) return;
    setFlow(LIST);
    setRenaming(null);
    setRemoving(null);
    for (const scope of SCOPES) dismissDialog(scope);
  }, [isAuthenticated, dismissDialog]);

  function openAdd() {
    setMessage(null);
    if (!passkeysSupported()) {
      setFlow({ step: "unsupported" });
      return;
    }
    stepUp.open("add");
  }

  function openRemove(passkey: Passkey) {
    setMessage(null);
    setRemoving(passkey);
    stepUp.open("remove");
  }

  function openRename(passkey: Passkey) {
    setMessage(null);
    openDialog("rename");
    setNewName(passkey.name ?? "");
    setRenaming(passkey);
  }

  function closeRename() {
    dismissDialog("rename");
    setRenaming(null);
  }

  async function onStepUp(e: FormEvent) {
    e.preventDefault();
    const kind = stepUp.action;
    const target = removing;
    if (!kind || busy) return;
    await run(kind, async (current) => {
      const grant = await stepUp.prove();
      if (!current()) return;
      if (kind === "add") {
        stepUp.close();
        // Start again, after a refused finish, keeps the name the user typed.
        // Maintainer, 2026-11-24: one neutral name for everyone, no device guess.
        if (flow.step !== "create") setCreateName(i18n.t("account:passkeyDefaultName"));
        openDialog("create");
        const first = !enrolled && list.data?.passkeys.length === 0;
        setFlow({ step: "create", first, phase: "ready", grant });
        return;
      }
      if (!target) return;
      await removePasskey(target.id, grant);
      list.retry();
      if (!current()) return;
      setMessage(i18n.t("account:passkeyRemovedMessage"));
      stepUp.close();
      setRemoving(null);
    }, { dialog: kind });
  }

  async function onRename(e: FormEvent) {
    e.preventDefault();
    const target = renaming;
    if (!target || busy || !newName.trim()) return;
    await run("rename", async (current) => {
      await renamePasskey(target.id, newName.trim());
      list.retry();
      if (current()) closeRename();
    });
  }

  // Moves the create step to another phase, if the page is still on it.
  function toPhase(next: CreatePhase) {
    setFlow((f) => (f.step === "create" ? { step: "create", first: f.first, ...next } : f));
  }

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    if (flow.step !== "create" || busy) return;
    if (flow.phase === "spent") {
      openAdd();
      return;
    }
    const { grant } = flow;
    const chosen = createName.trim();
    if (!chosen) return;
    await run("create", async (current) => {
      toPhase({ phase: "ready", grant });
      const { ceremony, options } = await startPasskeyRegistration();
      // A page the user has left opens no browser prompt and spends no grant.
      if (!current()) return;
      const credential = await createPasskey(options).catch((err: unknown) => {
        throw browserError(err);
      });
      if (!current()) return;
      if (credential === null) {
        toPhase({ phase: "cancelled", grant });
        return;
      }
      toPhase({ phase: "spent" });
      const passkey = await registerPasskey(
        { ceremony, credential, name: chosen }, grant,
      );
      list.retry();
      if (!current()) return;
      dismissDialog("create");
      setFlow({ step: "created", name: passkey.name ?? chosen });
    });
  }

  function leaveFlow() {
    dismissDialog("create");
    setFlow(LIST);
  }

  return {
    list, flow, stepUp, removing, renaming, newName, setNewName, createName, setCreateName, message, dialog,
    openAdd, openRemove, openRename, closeRename, onStepUp, onRename, onCreate, leaveFlow,
  };
}

export type Passkeys = ReturnType<typeof usePasskeys>;
