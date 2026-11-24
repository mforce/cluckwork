import { useCallback, useEffect, useState } from "react";
import type { FormEvent } from "react";
import { listPasskeys, renamePasskey, startPasskeyRegistration } from "../../api/cluckwork";
import type { Passkey } from "../../api/cluckwork";
import { NO_PROOF, registerPasskey, removePasskey, stepUp } from "../../api/client";
import type { StepUpProof } from "../../api/client";
import { useAuth } from "../../auth/useAuth";
import { useDialogAction } from "../../components/useDialogAction";
import { useLatestLoad } from "../../components/useLatestLoad";
import i18n from "../../i18n";
import { stale } from "./useTwoFactor";

// #1284 — the Security section's passkeys (record 1282). Add steps up first,
// then names the passkey and runs the browser's ceremony (#1283
// "Registration A"); Remove steps up in its dialog; Rename needs no step-up.
const SCOPES = ["add", "create", "rename", "remove"] as const;

// What the page shows. The grant lives only in the create step, which spends
// it on the finish; null once spent, so the next try steps up again.
export type PasskeyFlow =
  | { step: "list" }
  | { step: "create"; grant: string | null; first: boolean; cancelled: boolean }
  | { step: "created"; name: string }
  | { step: "unsupported" };

export type PasskeyDialog =
  | { kind: "add" }
  | { kind: "rename" | "remove"; passkey: Passkey };

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
  const list = useLatestLoad([credentialKey], listPasskeys);
  const [flow, setFlow] = useState<PasskeyFlow>(LIST);
  const [open, setOpen] = useState<PasskeyDialog | null>(null);
  const [proof, setProof] = useState<StepUpProof>(NO_PROOF);
  const [name, setName] = useState("");
  const [message, setMessage] = useState<string | null>(null);

  // #308 — a proof or a grant never outlives the session.
  useEffect(() => {
    if (isAuthenticated) return;
    setProof(NO_PROOF);
    setFlow(LIST);
    setOpen(null);
    for (const scope of SCOPES) dismissDialog(scope);
  }, [isAuthenticated, dismissDialog]);

  const openAdd = useCallback(() => {
    setMessage(null);
    if (!supported()) {
      setFlow({ step: "unsupported" });
      return;
    }
    openDialog("add");
    setOpen({ kind: "add" });
  }, [openDialog]);

  function openFor(kind: "rename" | "remove", passkey: Passkey) {
    setMessage(null);
    openDialog(kind);
    setName(passkey.name ?? "");
    setOpen({ kind, passkey });
  }

  // Every way out clears the proof (cancel, send, logout), so opening does not.
  function closeDialog() {
    setProof(NO_PROOF);
    if (open) dismissDialog(open.kind);
    setOpen(null);
  }

  async function prove() {
    const entered = proof;
    setProof(NO_PROOF);
    return (await stepUp(entered)).token;
  }

  async function onDialog(e: FormEvent) {
    e.preventDefault();
    const target = open;
    if (!target || busy) return;
    await run(target.kind, async (current) => {
      if (target.kind === "rename") {
        await renamePasskey(target.passkey.id, name.trim());
        list.retry();
        if (current()) closeDialog();
        return;
      }
      const grant = await prove();
      if (!current()) return;
      if (target.kind === "add") {
        closeDialog();
        setName(proposedName());
        openDialog("create");
        const first = !enrolled && list.data?.passkeys.length === 0;
        setFlow({ step: "create", grant, first, cancelled: false });
        return;
      }
      await removePasskey(target.passkey.id, grant).catch((err: unknown) => { throw stale(err); });
      list.retry();
      if (!current()) return;
      setMessage(i18n.t("account:passkeyRemovedMessage"));
      closeDialog();
    }, { dialog: target.kind });
  }

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    if (flow.step !== "create" || busy) return;
    if (flow.grant === null) {
      openAdd();
      return;
    }
    const { grant } = flow;
    const chosen = name.trim();
    await run("create", async (current) => {
      setFlow((f) => (f.step === "create" ? { ...f, cancelled: false } : f));
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
        if (current()) setFlow((f) => (f.step === "create" ? { ...f, cancelled: true } : f));
        return;
      }
      // The finish spends the grant, whatever it answers.
      setFlow((f) => (f.step === "create" ? { ...f, grant: null } : f));
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
    list, flow, open, proof, setProof, name, setName, message, dialog,
    openAdd, openFor, closeDialog, onDialog, onCreate, leaveFlow,
  };
}

export type Passkeys = ReturnType<typeof usePasskeys>;
