// #936 — the "current -> available" version handshake.
//
// "Current" is baked into THIS bundle at build time (VITE_APP_VERSION,
// release-please-owned via web/.env.production, #458) — the same constant
// AppLayout.tsx and errorReport.ts already read.
//
// "Available" (the WAITING worker's own version) cannot be read from the
// worker itself without restructuring the build off Workbox's `generateSW`
// strategy: there is no injected message channel, and postMessage to
// `registration.waiting` is not reliable on its own — a third deploy can
// make that worker `redundant` before it ever answers. Instead, the build
// emits a small `version.json` (vite.config.ts's versionManifest plugin),
// deliberately EXCLUDED from the Workbox precache glob (no `.json` in
// `workbox.globPatterns`) so a plain fetch is never intercepted and answered
// from the CURRENTLY ACTIVE worker's cache — `cache: "no-store"` then forces
// a real network round trip past the browser's own HTTP cache too.
export const CURRENT_VERSION = import.meta.env.VITE_APP_VERSION as string | undefined;

interface VersionPayload {
  version: string;
}

function isVersionPayload(value: unknown): value is VersionPayload {
  return (
    typeof value === "object" &&
    value !== null &&
    typeof (value as Record<string, unknown>).version === "string" &&
    (value as Record<string, unknown>).version !== ""
  );
}

/**
 * Reads the version the deployed build currently claims to be, or null if
 * the read fails, 404s (dev/test builds never emit this file), or the
 * payload is missing/malformed. Never guesses: a failed handshake means no
 * version is shown, rather than a stale, static, or placeholder one.
 */
export async function fetchAvailableVersion(signal?: AbortSignal): Promise<string | null> {
  try {
    const response = await fetch("/version.json", { cache: "no-store", signal });
    if (!response.ok) return null;
    const payload: unknown = await response.json();
    return isVersionPayload(payload) ? payload.version : null;
  } catch {
    return null;
  }
}

export interface VersionChange {
  current: string;
  available: string;
}

/**
 * Whether a "current -> available" line is worth showing at all, decided as
 * its own pure function so it is testable independent of CURRENT_VERSION's
 * module-scope env read (which, like AppLayout.tsx's identical pattern, is
 * simply unset in every test build). Deliberately withholds the line rather
 * than guessing: an unresolved current build, a failed/malformed handshake,
 * or two reads that happen to already agree are all "nothing worth showing",
 * never a placeholder or a stale number.
 */
export function describeVersionChange(
  current: string | undefined,
  available: string | null,
): VersionChange | null {
  if (current === undefined || available === null || available === current) return null;
  return { current, available };
}
