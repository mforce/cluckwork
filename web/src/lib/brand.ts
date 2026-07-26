// Per-farm accent palette (#149). The SECOND theming axis: `data-brand` is the
// farm's accent, chosen by an admin, while `data-theme` stays the user's own
// light/night choice. Neither forces the other.
//
// The API is the source of truth. localStorage exists ONLY so the pre-paint
// script (public/theme-init.js) can apply the palette before first paint; it is
// cleared on every path that ends a session, so farm A's colour never bleeds
// into farm B's login screen.
export const BRANDS = ["aubergine", "forest", "slate", "terracotta"] as const;
export type Brand = (typeof BRANDS)[number];

// Aubergine is the default AND carries no data-brand attribute, which is what
// makes an unknown id degrade to it with no validation on the CSS side.
export const DEFAULT_BRAND: Brand = "aubergine";

const KEY = "cluckwork.brand";

export function isBrand(value: string): value is Brand {
  return (BRANDS as readonly string[]).includes(value);
}

export function applyBrand(brand: string): void {
  // Attribute first: the palette must apply even if nothing persists.
  if (brand === DEFAULT_BRAND || !isBrand(brand)) {
    // An unknown id is treated as the default rather than written through: the
    // server rejects unknown ids on save, so seeing one here means stale cache
    // or tampering, and aubergine is the safe reading of both.
    delete document.documentElement.dataset.brand;
  } else {
    document.documentElement.dataset.brand = brand;
  }
  try {
    localStorage.setItem(KEY, brand);
  } catch {
    // Writes can fail while reads still succeed (quota exhaustion), leaving the
    // PREVIOUS palette cached — which would pre-paint the wrong farm colour on
    // the next load. Dropping the key falls back to the default instead.
    try {
      localStorage.removeItem(KEY);
    } catch {
      // storage fully unavailable; the attribute above is all we can do
    }
  }
}

export function initialBrand(): Brand {
  const set = document.documentElement.dataset.brand;
  return set !== undefined && isBrand(set) ? set : DEFAULT_BRAND;
}
