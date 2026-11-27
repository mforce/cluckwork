import { useCallback, useEffect, useEffectEvent, useRef, useState } from "react";
import { ApiError, startPasskeySignIn } from "../api/client";
import type { PasskeyAnswer, PasskeyCeremony } from "../api/client";
import { getPasskey, passkeysSupported, webAuthnHost } from "../auth/passkey";
import { useAuth } from "../auth/useAuth";
import i18n from "../i18n";
import { messageFor, PASSKEYS_OFF } from "./signInMessage";

// #1285 — passkey sign-in from the button and from autofill (record 1282
// decision 12). A ceremony lives 5 minutes by the server's clock, so the page
// renews it at 4 while visible, never while hidden, and once on return.
export const RENEW_MS = 4 * 60_000;
const CEREMONY_MS = 5 * 60_000;
// ponytail: a failed options request (not a 404 Passkeys.Off or a 429) is
// tried again on this fixed delay while visible; back off if it ever matters.
export const RETRY_MS = 30_000;
// The auth-passkey-options policy's default window: no Retry-After is waited
// out past it.
const MAX_WAIT_SECONDS = 900;

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

  // A ceremony the server has already expired is not offered while its
  // replacement is on the way: after a hidden page, a long second step, or a
  // renewal that hangs.
  const expire = useCallback(() => setState((s) =>
    (s.kind === "ready" && Date.now() >= s.dueAt - RENEW_MS + CEREMONY_MS ? { kind: "waiting", dueAt: s.dueAt } : s)), []);

  const renew = useCallback(async () => {
    if (renewing.current) return;
    renewing.current = true;
    try {
      const current = await startPasskeySignIn();
      // Off stays off, even when the step learned it during this request.
      setState((s) => (s.kind === "off" ? s : { kind: "ready", current, dueAt: Date.now() + RENEW_MS }));
    } catch (err) {
      setState((s) => afterFailure(s, err));
    } finally {
      renewing.current = false;
    }
  }, []);

  // The second step's options route answered 404 Passkeys.Off: the page
  // offers no passkey for the rest of its life.
  const markOff = useCallback(() => setState({ kind: "off" }), []);

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
    let expiry: number | undefined;
    const schedule = () => {
      clearTimeout(timer);
      clearTimeout(expiry);
      if (document.visibilityState === "hidden") return;
      timer = window.setTimeout(() => void renew(), Math.max(0, dueAt - Date.now()));
      // For a ceremony; a no-op in the other states.
      expiry = window.setTimeout(expire, Math.max(0, dueAt - RENEW_MS + CEREMONY_MS - Date.now()));
    };
    schedule();
    document.addEventListener("visibilitychange", schedule);
    return () => {
      clearTimeout(timer);
      clearTimeout(expiry);
      document.removeEventListener("visibilitychange", schedule);
    };
  }, [dueAt, renew, expire]);

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
    // An options route answered 404 Passkeys.Off.
    off: state.kind === "off",
    markOff,
    // The button and its caption show; the button works only when ready.
    offered: state.kind === "ready" || state.kind === "waiting" || state.kind === "limited",
    waiting: state.kind === "waiting",
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
