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
  | { kind: "ready"; current: PasskeyCeremony; dueAt: number; expiresAt: number }
  | { kind: "limited"; dueAt: number; minutes: number };

function afterFailure(state: State, err: unknown): State {
  // Off stays off for the page's life, whatever a request still in flight says.
  if (state.kind === "off") return state;
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
  // One passkey sign-in at a time, from the button or from autofill. The ref
  // answers within the same tick, before any re-render; the state renders.
  // hold keeps the two equal.
  const signingIn = useRef(false);
  const hold = useCallback((on: boolean) => {
    signingIn.current = on;
    setBusy(on);
  }, []);
  // Set after a refused autofill answer, until the user acts, so an
  // authenticator that answers by itself cannot sign in again and again.
  const [autofillPaused, setAutofillPaused] = useState(false);
  const renewing = useRef(false);
  const autofill = useRef<AbortController | null>(null);
  // An answer arrives after an await, so the farm code is read when it is
  // sent, not when the request started (record 1282 decision 11).
  const latestFarmCode = useRef(farmCode);
  useEffect(() => {
    latestFarmCode.current = farmCode;
  }, [farmCode]);

  // A ceremony the server has already expired is not offered while its
  // replacement is on the way: after a hidden page, a long second step, or a
  // renewal that hangs.
  const expire = useCallback(() => setState((s) =>
    (s.kind === "ready" && Date.now() >= s.expiresAt ? { kind: "waiting", dueAt: s.dueAt } : s)), []);

  const renew = useCallback(async () => {
    if (renewing.current) return;
    renewing.current = true;
    // A 429 note clears while the next request is out, so a second 429 with
    // the same wait changes the live region again and is announced.
    setState((s) => (s.kind === "limited" ? { kind: "waiting", dueAt: s.dueAt } : s));
    try {
      const current = await startPasskeySignIn();
      // Off stays off, even when the step learned it during this request.
      const madeAt = Date.now();
      setState((s) => (s.kind === "off" ? s : { kind: "ready", current, dueAt: madeAt + RENEW_MS, expiresAt: madeAt + CEREMONY_MS }));
    } catch (err) {
      setState((s) => afterFailure(s, err));
    } finally {
      renewing.current = false;
    }
  }, []);

  // The second step's options route answered 404 Passkeys.Off: the page
  // offers no passkey for the rest of its life.
  const markOff = useCallback(() => setState({ kind: "off" }), []);

  // Called holding the sign-in; a success keeps it held: the page is leaving.
  async function finish(answer: PasskeyAnswer, fromAutofill: boolean) {
    onError(null);
    try {
      await passkeyLogin(latestFarmCode.current, answer);
    } catch (err) {
      onError(messageFor(err));
      if (fromAutofill) setAutofillPaused(true);
      // The server may have spent the ceremony: nothing uses it again. Off
      // and a 429's wait, learned meanwhile, stand.
      setState((s) => (s.kind === "off" || (err instanceof ApiError && err.title === PASSKEYS_OFF) ? { kind: "off" }
        : s.kind === "limited" ? s : { kind: "waiting", dueAt: 0 }));
      hold(false);
    }
  }

  // No options request while a sign-in is in flight.
  const dueAt = active && !busy && "dueAt" in state ? state.dueAt : null;
  const expiresAt = state.kind === "ready" ? state.expiresAt : null;
  useEffect(() => {
    if (dueAt === null) return;
    let timer: number | undefined;
    let expiry: number | undefined;
    const schedule = () => {
      clearTimeout(timer);
      clearTimeout(expiry);
      if (document.visibilityState === "hidden") return;
      timer = window.setTimeout(() => void renew(), Math.max(0, dueAt - Date.now()));
      if (expiresAt !== null) expiry = window.setTimeout(expire, Math.max(0, expiresAt - Date.now()));
    };
    schedule();
    document.addEventListener("visibilitychange", schedule);
    return () => {
      clearTimeout(timer);
      clearTimeout(expiry);
      document.removeEventListener("visibilitychange", schedule);
    };
  }, [dueAt, expiresAt, renew, expire]);

  const listen = useEffectEvent(async (current: PasskeyCeremony, signal: AbortSignal) => {
    if (!(await window.PublicKeyCredential?.isConditionalMediationAvailable?.()) || signal.aborted) return;
    const answer = await getPasskey(current, { signal, conditional: true }).catch(() => null);
    // Aborted meanwhile, by the password submit or the button: that one wins.
    if (!answer || signal.aborted) return;
    hold(true);
    await finish(answer, true);
  });

  // A ceremony past its life is not offered even before the expiry timer
  // runs, as on return from a long second step.
  const fresh = state.kind === "ready" && Date.now() < state.expiresAt ? state.current : null;

  // Autofill: a conditional request on the current ceremony, aborted and
  // started again whenever the ceremony changes.
  const listening = active && !busy && !autofillPaused ? fresh : null;
  useEffect(() => {
    if (listening === null) return;
    const controller = new AbortController();
    autofill.current = controller;
    void listen(listening, controller.signal);
    return () => controller.abort();
  }, [listening]);

  async function signInWithButton() {
    if (fresh === null || signingIn.current || !active) return;
    // The render can be older than the clock, after a hidden tab or a sleep.
    if (state.kind === "ready" && Date.now() >= state.expiresAt) {
      expire();
      return;
    }
    hold(true);
    // The browser runs one request at a time, so autofill stops first.
    autofill.current?.abort();
    setAutofillPaused(false);
    onError(null);
    let answer: PasskeyAnswer | null;
    try {
      answer = await getPasskey(fresh);
    } catch (err) {
      onError(messageFor(err));
      hold(false);
      return;
    }
    if (answer) return finish(answer, false);
    onError(i18n.t("auth:passkeySignInCancelled"));
    hold(false);
  }

  return {
    // An options route answered 404 Passkeys.Off.
    off: state.kind === "off",
    markOff,
    // The button and its caption show; the button works only when ready.
    offered: state.kind === "ready" || state.kind === "waiting" || state.kind === "limited",
    waiting: state.kind === "waiting",
    enabled: fresh !== null && active && !busy,
    // The email field's webauthn token.
    autofill: fresh !== null,
    limitedMinutes: state.kind === "limited" ? state.minutes : null,
    busy,
    signInWithButton,
    // The user typed in or focused the email field: autofill may run again.
    resumeAutofill: () => setAutofillPaused(false),
  };
}

export type PasskeySignIn = ReturnType<typeof usePasskeySignIn>;
