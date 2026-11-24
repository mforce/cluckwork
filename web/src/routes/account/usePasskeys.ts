import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { listPasskeys, registerPasskey, removePasskey, renamePasskey, startPasskeyRegistration } from "../../api/cluckwork";
import type { Passkey } from "../../api/cluckwork";
import { useAuth } from "../../auth/useAuth";
import { useDialogAction } from "../../components/useDialogAction";
import { useLatestLoad } from "../../components/useLatestLoad";
import i18n from "../../i18n";
import { stale, useStepUpDialog } from "./useStepUpDialog";

// #1284 — the Security section's passkeys (record 1282). Add steps up first,
// then names the passkey and runs the browser's ceremony (#1283
// "Registration A"); Remove steps up in its dialog; Rename needs no step-up.
const SCOPES = ["add", "remove", "rename", "create"] as const;

// The create step after the step-up. Its grant is spent by the finish
// whatever it answers, so a sent finish leaves the step "spent", and the next
// try steps up again; a browser cancel sends nothing and keeps the grant.
export type CreateStep = { step: "create"; first: boolean } & (
  | { phase: "ready" | "cancelled"; grant: string }
  | { phase: "spent" });

export type PasskeyFlow =
  | { step: "list" }
  | CreateStep
  | { step: "created"; name: string }
  | { step: "unsupported" };

const LIST: PasskeyFlow = { step: "list" };

// The JSON helpers are the whole of the browser support this needs: the
// options arrive as JSON and the credential goes back as JSON.
const supported = () => typeof window.PublicKeyCredential?.parseCreationOptionsFromJSON === "function";

// The name the create step proposes, which the user can change.
function proposedName(): string {
  const ua = navigator.userAgent;
  return i18n.t(/iPad|Tablet/.test(ua) ? "account:passkeyNameTablet"
    : /Mobi|Android|iPhone/.test(ua) ? "account:passkeyNamePhone" : "account:passkeyNameComputer");
}

// The browser's refusals, as the create step says them. NotAllowedError, a
// cancel or a timeout, is not one: the step shows it as a cancel.
function browserError(err: unknown): unknown {
  if (!(err instanceof DOMException)) return err;
  return new Error(i18n.t(err.name === "InvalidStateError" ? "account:passkeyExists" : "account:passkeyFailed"));
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
    for (const scope of SCOPES) dismissDialog(scope);
  }, [isAuthenticated, dismissDialog]);

  function openAdd() {
    setMessage(null);
    if (!supported()) {
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
        setCreateName(proposedName());
        openDialog("create");
        const first = !enrolled && list.data?.passkeys.length === 0;
        setFlow({ step: "create", first, phase: "ready", grant });
        return;
      }
      await removePasskey(target!.id, grant).catch((err: unknown) => { throw stale(err); });
      list.retry();
      if (!current()) return;
      setMessage(i18n.t("account:passkeyRemovedMessage"));
      stepUp.close();
    }, { dialog: kind });
  }

  async function onRename(e: FormEvent) {
    e.preventDefault();
    const target = renaming;
    if (!target || busy) return;
    await run("rename", async (current) => {
      await renamePasskey(target.id, newName.trim());
      list.retry();
      if (current()) closeRename();
    });
  }

  // Moves the create step to another phase, if the page is still on it.
  function toPhase(phase: CreateStep["phase"], grant: string) {
    setFlow((f) => (f.step !== "create" ? f
      : phase === "spent" ? { step: "create", first: f.first, phase }
      : { step: "create", first: f.first, phase, grant }));
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
    await run("create", async (current) => {
      toPhase("ready", grant);
      const { ceremony, options } = await startPasskeyRegistration();
      let credential: PublicKeyCredential | null;
      try {
        credential = await navigator.credentials.create({
          publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(options),
        }) as PublicKeyCredential | null;
      } catch (err) {
        if (!(err instanceof DOMException && err.name === "NotAllowedError")) throw browserError(err);
        credential = null;
      }
      if (credential === null) {
        if (current()) toPhase("cancelled", grant);
        return;
      }
      toPhase("spent", grant);
      const passkey = await registerPasskey(
        { ceremony, credential: credential.toJSON() as RegistrationResponseJSON, name: chosen }, grant,
      ).catch((err: unknown) => { throw stale(err); });
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
