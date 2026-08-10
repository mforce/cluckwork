import { useEffect, useState } from "react";
import { getFarmBanner, getFarmLogo } from "../api/cluckwork";

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

// A branding image (logo or banner, #179) as an object URL.
//
// The image is FETCHED rather than linked: /account/logo and /account/banner
// both sit behind the Authorization header and an <img src> cannot carry one,
// so the bytes come through the API client and render from a blob: URL —
// which is why the CSP carries `img-src 'self' blob:` (SecurityHeaders.cs).
//
// `hash` is the dependency because it changes exactly when the image does: an
// unrelated settings save does not re-fetch a megabyte, and a replacement
// does. Passing null (no image set) skips the request entirely.
function useImageObjectUrl(
  hash: string | null,
  fetchImage: () => Promise<{ blob: Blob }>,
): FarmLogoImage {
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
    if (hash === null) {
      setUrl(null);
      return;
    }

    let objectUrl: string | null = null;
    let cancelled = false;

    fetchImage()
      .then(({ blob }) => {
        if (cancelled) return;
        objectUrl = URL.createObjectURL(blob);
        setUrl(objectUrl);
        setSettledHash(hash);
      })
      .catch(() => {
        if (cancelled) return;
        // Reported through `failed` rather than thrown: callers show their own
        // fallback, and an image that will not load is not worth taking a
        // screen down over.
        setFailedHash(hash);
        setSettledHash(hash);
      });

    return () => {
      cancelled = true;
      // Cleared as the hash changes, not only at unmount: leaving the old URL
      // in state would point an <img> at bytes about to be revoked.
      setUrl(null);
      if (objectUrl !== null) URL.revokeObjectURL(objectUrl);
    };
    // fetchImage is a stable module-level function reference (getFarmLogo /
    // getFarmBanner) for every real caller, so it is intentionally not in the
    // dependency list — including it would be correct too, but every caller
    // would then need useCallback to avoid an infinite refetch loop for no gain.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hash]);

  return {
    url,
    // Derived, so it is true from the very first render on which there is an
    // image to fetch — not one commit later.
    loading: hash !== null && settledHash !== hash,
    failed: hash !== null && failedHash === hash,
  };
}

// The farm logo as an object URL (#123). Thin wrapper so every existing
// caller/test keeps its exact signature; useBannerObjectUrl below is the
// sibling for #179.
export function useLogoObjectUrl(logoHash: string | null): FarmLogoImage {
  return useImageObjectUrl(logoHash, getFarmLogo);
}

// The farm banner as an object URL (#179) — same contract as the logo above,
// for the post-login splash.
export function useBannerObjectUrl(bannerHash: string | null): FarmLogoImage {
  return useImageObjectUrl(bannerHash, getFarmBanner);
}
