import { useCallback, useEffect, useEffectEvent, useRef, useState } from "react";
import { ApiError, startPasskeySignIn } from "../api/client";
import type { PasskeyAnswer, PasskeyCeremony } from "../api/client";
import { getPasskey, passkeysSupported } from "../auth/passkey";
import { useAuth } from "../auth/useAuth";
import i18n from "../i18n";
import { messageFor } from "./signInMessage";

// #1285 — passkey sign-in from the button and from autofill (record 1282
// decision 12). A ceremony lives 5 minutes by the server's clock, so the page
// renews it at 4 while visible, never while hidden, and once on return.
export const RENEW_MS = 4 * 60_000;

// loading: the first options request is out. off: this browser cannot, or
// the server answered 404 Passkeys.Off; the page offers no passkey at all.
// limited: a 429 from auth-passkey-options; the button stays, disabled.
export type PasskeySignIn =
  | { kind: "loading" }
  | { kind: "off" }
  | { kind: "ready"; current: PasskeyCeremony; madeAt: number }
  | { kind: "limited"; until: number; minutes: number };

const PASSKEY_REFUSED = "Auth.PasskeyRefused";

interface Options {
  // False while the second step shows: no autofill request, no renewal.
  active: boolean;
  farmCode: string;
  onError: (message: string | null) => void;
}

// A completed sign-in flips isAuthenticated, and the Login page's effect then
// takes the user to where they were going.
export function usePasskeySignIn({ active, farmCode, onError }: Options) {
  const { passkeyLogin } = useAuth();
  const [state, setState] = useState<PasskeySignIn>(() => (passkeysSupported() ? { kind: "loading" } : { kind: "off" }));
  const [busy, setBusy] = useState(false);
  const renewing = useRef(false);
  const autofill = useRef<AbortController | null>(null);

  const renew = useCallback(async () => {
    if (renewing.current) return;
    renewing.current = true;
    try {
      const current = await startPasskeySignIn();
      setState({ kind: "ready", current, madeAt: Date.now() });
    } catch (err) {
      if (err instanceof ApiError && err.status === 429) {
        const seconds = err.retryAfterSeconds ?? 60;
        setState({ kind: "limited", until: Date.now() + seconds * 1000, minutes: Math.ceil(seconds / 60) });
      } else {
        // ponytail: any other failure turns passkeys off until a reload; the
        // password still works.
        setState({ kind: "off" });
      }
    } finally {
      renewing.current = false;
    }
  }, []);

  async function finish(answer: PasskeyAnswer) {
    setBusy(true);
    onError(null);
    try {
      await passkeyLogin(farmCode, answer);
    } catch (err) {
      onError(err instanceof ApiError && err.title === PASSKEY_REFUSED ? i18n.t("errors:Auth.PasskeyRefused") : messageFor(err));
      // The server may have spent the ceremony.
      void renew();
    } finally {
      setBusy(false);
    }
  }

  // The next renewal: due at once while loading, at 4 minutes for a ceremony,
  // and at Retry-After when limited.
  const dueAt = !active ? null
    : state.kind === "loading" ? 0
      : state.kind === "ready" ? state.madeAt + RENEW_MS
        : state.kind === "limited" ? state.until : null;
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
    if (!(await PublicKeyCredential.isConditionalMediationAvailable?.()) || signal.aborted) return;
    const answer = await getPasskey(current, { signal, conditional: true }).catch(() => null);
    if (answer) await finish(answer);
  });

  // Autofill: a conditional request on the current ceremony, aborted and
  // started again whenever the ceremony changes.
  const listening = active && !busy && state.kind === "ready" ? state.current : null;
  useEffect(() => {
    if (listening === null) return;
    const controller = new AbortController();
    autofill.current = controller;
    void listen(listening, controller.signal);
    return () => controller.abort();
  }, [listening]);

  async function signInWithButton() {
    if (state.kind !== "ready" || busy) return;
    // The browser runs one request at a time, so autofill stops first.
    autofill.current?.abort();
    setBusy(true);
    onError(null);
    let answer: PasskeyAnswer | null = null;
    try {
      answer = await getPasskey(state.current);
    } catch {
      // Treated as a cancel: the browser refused before asking the user.
    }
    if (answer) await finish(answer);
    else {
      setBusy(false);
      onError(i18n.t("auth:passkeySignInCancelled"));
    }
  }

  return { state, busy, signInWithButton };
}
