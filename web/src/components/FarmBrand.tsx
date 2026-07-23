import { Egg } from "lucide-react";
import { useFarm } from "../farm/useFarm";
import { useLogoObjectUrl } from "../farm/useLogoObjectUrl";

// The chrome's branding slot (#123): the farm's own name, and its logo when
// one is set. Falls back to the app's egg mark when there is no logo, and —
// before /account has answered at all — to the app's name too.
export function FarmBrand() {
  const { farm } = useFarm();
  const logoUrl = useLogoObjectUrl(farm?.logoContentHash ?? null);

  return (
    <span className="brand">
      {logoUrl === null ? (
        <Egg size={20} aria-hidden className="brand-mark" />
      ) : (
        // Empty alt on purpose: the farm name sits right beside it, so the
        // image is decoration and a screen reader should not read the farm
        // twice.
        <img className="brand-logo" src={logoUrl} alt="" />
      )}
      <span className="brand-name">{farm?.name ?? "Cluckwork"}</span>
    </span>
  );
}
