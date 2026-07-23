import { useEffect, useState } from "react";
import { getFarmLogo } from "../api/cluckwork";

// The farm logo as an object URL, or null when there is none (#123).
//
// The logo is FETCHED rather than linked: /account/logo sits behind the
// Authorization header and an <img src> cannot carry one, so the bytes come
// through the API client and render from a blob: URL — which is why the CSP
// carries `img-src 'self' blob:` (SecurityHeaders.cs).
//
// `logoHash` is the dependency because it changes exactly when the logo does:
// an unrelated settings save does not re-fetch a megabyte, and a replacement
// does. Passing null (no logo set) skips the request entirely.
export function useLogoObjectUrl(logoHash: string | null): string | null {
  const [url, setUrl] = useState<string | null>(null);

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
      })
      .catch(() => {
        // Callers show their own fallback. A logo that will not load is not
        // worth an error banner over the whole shell.
      });

    return () => {
      cancelled = true;
      // Cleared as the hash changes, not only at unmount: leaving the old URL
      // in state would point an <img> at bytes about to be revoked.
      setUrl(null);
      if (objectUrl !== null) URL.revokeObjectURL(objectUrl);
    };
  }, [logoHash]);

  return url;
}
