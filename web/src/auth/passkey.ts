import type { PasskeyAnswer, PasskeyCeremony } from "../api/client";

// #1285 — the browser half of a passkey sign-in. The options arrive as JSON
// and the credential goes back as JSON, so these helpers are all it needs.
export const passkeysSupported = () =>
  typeof window.PublicKeyCredential?.parseRequestOptionsFromJSON === "function";

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
    if (err instanceof DOMException && (err.name === "NotAllowedError" || err.name === "AbortError")) return null;
    throw err;
  }
}
