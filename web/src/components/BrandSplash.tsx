import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";
import { useBannerObjectUrl } from "../farm/useLogoObjectUrl";

interface BrandSplashProps {
  farmName: string;
  bannerContentHash: string;
  onDismiss: () => void;
}

// #179 — the post-login splash: a full-size showing of the farm's banner,
// once per sign-in, before the authenticated shell is used.
//
// Deliberately NOT built on Dialog (web/src/components/Dialog.tsx). There is
// never more than one of these open — it renders once, before any screen
// exists to open its own dialog — so Dialog's multi-instance hazards (#482)
// do not apply, and Dialog's title/body shape does not fit a bare hero image.
// The caller (SessionContext.tsx) decides WHETHER to render this at all (no
// banner set → never mounted); this component only decides when to stop
// showing what it was given.
export function BrandSplash({ farmName, bannerContentHash, onDismiss }: BrandSplashProps) {
  const { t } = useTranslation("splash");
  const { url, failed } = useBannerObjectUrl(bannerContentHash);
  const continueRef = useRef<HTMLButtonElement>(null);

  // The only focusable control, so a full focus trap is unnecessary — nothing
  // behind this overlay is reachable while `inert` (set by the caller) holds,
  // and Escape has nothing meaningful to cancel (this isn't dismissing a
  // half-entered form). Continue is the one way out, and gets focus so a
  // keyboard user is not dropped on <body>.
  useEffect(() => {
    continueRef.current?.focus();
  }, []);

  // A banner that will not load is not worth a screen for — skip straight to
  // the shell rather than showing an empty splash with nothing on it.
  useEffect(() => {
    if (failed) onDismiss();
  }, [failed, onDismiss]);

  if (failed) return null;

  return (
    <div className="brand-splash-backdrop" role="dialog" aria-modal="true" aria-label={farmName}>
      <div className="brand-splash">
        {/* No loading/empty placeholder: the Continue button is available
            immediately regardless of the fetch, so a slow banner never blocks
            the farm from reaching the dashboard (#179 review). */}
        {url !== null && (
          <img className="brand-splash-image" src={url} alt={t("bannerAlt", { farmName })} />
        )}
        {/* Plain button, no className: the base button style already reads as
            the primary action (repo convention — "link" is the secondary/
            cancel style, "btn-danger" the destructive one, see useConfirm.tsx). */}
        <button type="button" ref={continueRef} className="brand-splash-continue"
          onClick={onDismiss}>
          {t("continue")}
        </button>
      </div>
    </div>
  );
}
