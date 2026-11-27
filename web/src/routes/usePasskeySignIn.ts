import { useCallback, useEffect, useEffectEvent, useRef, useState } from "react";
import { ApiError, startPasskeySignIn } from "../api/client";
import type { PasskeyAnswer, PasskeyCeremony } from "../api/client";
import { getPasskey, passkeysSupported } from "../auth/passkey";
import { useAuth } from "../auth/useAuth";
import i18n from "../i18n";
import { messageFor, PASSKEYS_OFF } from "./signInMessage";

// #1285 — passkey sign-in from the button and from autofill (record 1282
// decision 12). A ceremony lives 5 minutes by the server's clock, so the page
// renews it at 4 while visible, never while hidden, and once on return.
export const RENEW_MS = 4 * 60_000;
// ponytail: a failed options request (not a 404 Passkeys.Off or a 429) is
// tried again on this fixed delay while visible; back off if it ever matters.
export const RETRY_MS = 30_000;
// The auth-passkey-options window: no Retry-After is waited out past it.
const MAX_WAIT_SECONDS = 900;

// WebAuthn refuses an IP address as the relying party, so such a host never
// runs a ceremony and asks for none.
export const webAuthnHost = (host: string) => !/^\d{1,3}(\.\d{1,3}){3}$/.test(host) && !host.startsWith("[");

// unsupported: this browser or host cannot; off: the options route answered
// 404 Passkeys.Off. Neither shows any passkey UI. loading: no ceremony yet,
// nothing shown. waiting: the ceremony was spent, or a renewal failed; the
// button shows, disabled, until the next one. limited: a 429.
type State =
  | { kind: "unsupported" }
  | { kind: "off" }
  | { kind: "loading"; dueAt: number }
  | { kind: "waiting"; dueAt: number }
  | { kind: "ready"; current: PasskeyCeremony; dueAt: number }
  | { kind: "limited"; dueAt: number; minutes: number };

function afterFailure(state: State, err: unknown): State {
  if (err instanceof ApiError && err.title === PASSKEYS_OFF) return { kind: "off" };
  if (err instanceof ApiError && err.status === 429) {
    const seconds = Math.min(err.retryAfterSeconds ?? 60, MAX_WAIT_SECONDS);
    return { kind: "limited", dueAt: Date.now() + seconds * 1000, minutes: Math.ceil(seconds / 60) };
  }
  const dueAt = Date.now() + RETRY_MS;
  return state.kind === "loading" ? { kind: "loading", dueAt } : { kind: "waiting", dueAt };
}

interface Options {
  // False while the second step shows or the password is being checked: no
  // autofill request, no renewal, no button.
  active: boolean;
  farmCode: string;
  onError: (message: string | null) => void;
}

// A completed sign-in flips isAuthenticated, and the Login page's effect then
// takes the user to where they were going.
export function usePasskeySignIn({ active, farmCode, onError }: Options) {
  const { passkeyLogin } = useAuth();
  const [state, setState] = useState<State>(() =>
    passkeysSupported() && webAuthnHost(window.location.hostname) ? { kind: "loading", dueAt: 0 } : { kind: "unsupported" });
  const [busy, setBusy] = useState(false);
  // Set after a refused autofill answer, until the user acts, so an
  // authenticator that answers by itself cannot sign in again and again.
  const [autofillPaused, setAutofillPaused] = useState(false);
  const renewing = useRef(false);
  const autofill = useRef<AbortController | null>(null);

  const renew = useCallback(async () => {
    if (renewing.current) return;
    renewing.current = true;
    try {
      const current = await startPasskeySignIn();
      setState({ kind: "ready", current, dueAt: Date.now() + RENEW_MS });
    } catch (err) {
      setState((s) => afterFailure(s, err));
    } finally {
      renewing.current = false;
    }
  }, []);

  // A success leaves busy set: the page is leaving.
  async function finish(answer: PasskeyAnswer, fromAutofill: boolean) {
    setBusy(true);
    onError(null);
    try {
      await passkeyLogin(farmCode, answer);
    } catch (err) {
      onError(messageFor(err));
      if (fromAutofill) setAutofillPaused(true);
      // The server may have spent the ceremony: nothing uses it again.
      setState({ kind: "waiting", dueAt: 0 });
      setBusy(false);
    }
  }

  const dueAt = active && "dueAt" in state ? state.dueAt : null;
  useEffect(() => {
    if (dueAt === null) return;
    let timer: number | undefined;
    const schedule = () => {
      clearTimeout(timer);
      if (document.visibilityState !== "hidden") timer = window.setTimeout(() => void renew(), Math.max(0, dueAt - Date.now()));
    };
    schedule();
    document.addEventListener("visibilitychange", schedule);
    return () => {
      clearTimeout(timer);
      document.removeEventListener("visibilitychange", schedule);
    };
  }, [dueAt, renew]);

  const listen = useEffectEvent(async (current: PasskeyCeremony, signal: AbortSignal) => {
    if (!(await window.PublicKeyCredential?.isConditionalMediationAvailable?.()) || signal.aborted) return;
    const answer = await getPasskey(current, { signal, conditional: true }).catch(() => null);
    if (answer) await finish(answer, true);
  });

  // Autofill: a conditional request on the current ceremony, aborted and
  // started again whenever the ceremony changes.
  const listening = active && !busy && !autofillPaused && state.kind === "ready" ? state.current : null;
  useEffect(() => {
    if (listening === null) return;
    const controller = new AbortController();
    autofill.current = controller;
    void listen(listening, controller.signal);
    return () => controller.abort();
  }, [listening]);

  async function signInWithButton() {
    if (state.kind !== "ready" || busy || !active) return;
    // The browser runs one request at a time, so autofill stops first.
    autofill.current?.abort();
    setAutofillPaused(false);
    setBusy(true);
    onError(null);
    try {
      const answer = await getPasskey(state.current);
      if (answer) return await finish(answer, false);
      onError(i18n.t("auth:passkeySignInCancelled"));
    } catch (err) {
      onError(messageFor(err));
    }
    setBusy(false);
  }

  return {
    // The button and its caption show; the button works only when ready.
    offered: state.kind === "ready" || state.kind === "waiting" || state.kind === "limited",
    enabled: state.kind === "ready" && active && !busy,
    // The email field's webauthn token.
    autofill: state.kind === "ready",
    limitedMinutes: state.kind === "limited" ? state.minutes : null,
    busy,
    signInWithButton,
    // The user typed in or focused the email field: autofill may run again.
    resumeAutofill: () => setAutofillPaused(false),
  };
}

export type PasskeySignIn = ReturnType<typeof usePasskeySignIn>;
