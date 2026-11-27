import type { PasskeyAnswer, PasskeyCeremony } from "../api/client";
import i18n from "../i18n";

// #1284/#1285 — the browser half of a passkey: the one module that calls
// navigator.credentials. Options arrive as JSON and credentials go back as
// JSON, so the JSON helpers are the whole of the support this needs.
export const passkeysSupported = () =>
  typeof window.PublicKeyCredential?.parseRequestOptionsFromJSON === "function"
  && typeof window.PublicKeyCredential.parseCreationOptionsFromJSON === "function";

// WebAuthn refuses an IP address as the relying party, and the server runs
// no ceremony for one (record 1282 decision 5), so such a host offers none.
export const webAuthnHost = (host: string) => !/^\d{1,3}(\.\d{1,3}){3}$/.test(host) && !host.startsWith("[");

// The browser or the authenticator failed, as opposed to the user cancelling.
// Its message is the one text every passkey surface shows for it.
export class PasskeyBrowserError extends Error {
  constructor(cause: unknown) {
    super(i18n.t("auth:passkeyBrowserFailed"), { cause });
  }
}

const isDomError = (err: unknown, ...names: string[]) => err instanceof DOMException && names.includes(err.name);

// Null when the user cancels or the request is aborted.
export async function getPasskey(
  { ceremony, options }: PasskeyCeremony, init: { signal?: AbortSignal; conditional?: boolean } = {},
): Promise<PasskeyAnswer | null> {
  try {
    const credential = await navigator.credentials.get({
      publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(options),
      signal: init.signal,
      ...(init.conditional ? { mediation: "conditional" as const } : {}),
    }) as PublicKeyCredential | null;
    return credential && { ceremony, credential: credential.toJSON() as AuthenticationResponseJSON };
  } catch (err) {
    if (isDomError(err, "NotAllowedError", "AbortError")) return null;
    throw new PasskeyBrowserError(err);
  }
}

// Null when the user cancels. Other errors are rethrown as the browser gave
// them, because registration names InvalidStateError (a passkey already on
// this device) apart.
export async function createPasskey(options: PublicKeyCredentialCreationOptionsJSON): Promise<RegistrationResponseJSON | null> {
  try {
    const credential = await navigator.credentials.create({
      publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(options),
    }) as PublicKeyCredential | null;
    return credential && credential.toJSON() as RegistrationResponseJSON;
  } catch (err) {
    if (isDomError(err, "NotAllowedError")) return null;
    throw err;
  }
}
