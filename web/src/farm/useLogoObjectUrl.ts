import { useEffect, useState } from "react";
import { getFarmLogo } from "../api/cluckwork";

export interface FarmLogoImage {
  // The object URL to render, or null while it is loading, when the farm has
  // no logo, and when the fetch failed.
  url: string | null;
  // In flight. Distinguished from "none" so a screen does not announce that a
  // farm has no logo while its logo is still on the wire (review of #123).
  loading: boolean;
  // The farm HAS a logo and the bytes would not come. Also distinguished from
  // "none": saying "no logo set" beside a Remove button is a contradiction the
  // reader has no way to resolve.
  failed: boolean;
}

// The farm logo as an object URL (#123).
//
// The logo is FETCHED rather than linked: /account/logo sits behind the
// Authorization header and an <img src> cannot carry one, so the bytes come
// through the API client and render from a blob: URL — which is why the CSP
// carries `img-src 'self' blob:` (SecurityHeaders.cs).
//
// `logoHash` is the dependency because it changes exactly when the logo does:
// an unrelated settings save does not re-fetch a megabyte, and a replacement
// does. Passing null (no logo set) skips the request entirely.
export function useLogoObjectUrl(logoHash: string | null): FarmLogoImage {
  const [url, setUrl] = useState<string | null>(null);
  // The hash whose request has finished, whichever way it went. Compared
  // against the CURRENT hash to derive `loading` — a `loading` boolean set
  // inside the effect is false for the commit in which the hash first becomes
  // non-null, so a farm that has a logo rendered as one that has none for a
  // frame, beside its own Remove button (round 2: codex, and two agents, one
  // of which reproduced it as an intermittent test failure).
  const [settledHash, setSettledHash] = useState<string | null>(null);
  const [failedHash, setFailedHash] = useState<string | null>(null);

  useEffect(() => {
    if (logoHash === null) {
      setUrl(null);
      return;
    }

    let objectUrl: string | null = null;
    let cancelled = false;

    getFarmLogo()
      .then(({ blob }) => {
        if (cancelled) return;
        objectUrl = URL.createObjectURL(blob);
        setUrl(objectUrl);
        setSettledHash(logoHash);
      })
      .catch(() => {
        if (cancelled) return;
        // Reported through `failed` rather than thrown: callers show their own
        // fallback, and a logo that will not load is not worth taking a screen
        // down over.
        setFailedHash(logoHash);
        setSettledHash(logoHash);
      });

    return () => {
      cancelled = true;
      // Cleared as the hash changes, not only at unmount: leaving the old URL
      // in state would point an <img> at bytes about to be revoked.
      setUrl(null);
      if (objectUrl !== null) URL.revokeObjectURL(objectUrl);
    };
  }, [logoHash]);

  return {
    url,
    // Derived, so it is true from the very first render on which there is a
    // logo to fetch — not one commit later.
    loading: logoHash !== null && settledHash !== logoHash,
    failed: logoHash !== null && failedHash === logoHash,
  };
}
