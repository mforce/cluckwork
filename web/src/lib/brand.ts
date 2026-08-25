// Per-farm accent palette (#149, per-farm since #586). The SECOND theming axis:
// `data-brand` is the farm's accent, chosen by an admin, while `data-theme`
// stays the user's own light/night choice. Neither forces the other.
//
// The API is the source of truth. localStorage exists ONLY so the pre-paint
// script (public/theme-init.js) can apply the palette before first paint, and
// it is keyed PER FARM: `cluckwork.brand:<slug>`. A key is written only under a
// slug the current session's login typed, and only while that login's account
// is still the tab's bound account — enforced by the comparison in
// getBoundFarmCode(), not by this comment. A tab with no such binding (a fresh
// tab restored from the refresh cookie) applies the palette and caches nothing,
// so its cache can be STALE, but it can never hold another farm's colour.
import { getBoundFarmCode } from "../auth/tokenStore";

export const BRANDS = ["aubergine", "forest", "slate", "terracotta"] as const;
export type Brand = (typeof BRANDS)[number];

// Aubergine is the default AND carries no data-brand attribute, which is what
// makes an unknown id degrade to it with no validation on the CSS side.
export const DEFAULT_BRAND: Brand = "aubergine";

// Pre-#586: one un-namespaced key for the whole device. Still READ at pre-paint
// as a fallback for a farm the device can name, and deleted the moment that farm
// has a key of its own.
const LEGACY_KEY = "cluckwork.brand";

// #586 — one key per farm. The prefix lives here because this module owns the
// brand namespace; farmCodeCache imports brandKeyFor rather than rebuilding it.
const KEY_PREFIX = "cluckwork.brand:";

export function brandKeyFor(slug: string): string {
  return KEY_PREFIX + slug;
}

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
  // #586 — cache ONLY under a slug this session's login typed, and only while
  // that login's account is still the tab's bound account. A fresh tab restored
  // from the refresh cookie has no such binding: it paints, and caches nothing.
  // The cost is a stale pre-paint until this device's next explicit login; the
  // thing it buys is that another farm's colour can never reach this key.
  const slug = getBoundFarmCode();
  if (slug === null) return;
  const key = brandKeyFor(slug);
  try {
    localStorage.setItem(key, brand);
  } catch {
    // Writes can fail while reads still succeed (quota exhaustion), leaving the
    // PREVIOUS palette cached — which would pre-paint the wrong colour on the
    // next load. Dropping the key falls back to the default instead.
    try {
      localStorage.removeItem(key);
    } catch {
      // storage fully unavailable; the attribute above is all we can do
    }
    return;
  }
  // Its own try, and only after a SUCCESSFUL write: this farm now has a real
  // key, so the un-namespaced fallback is superseded for this device. A failure
  // here must not undo the write above.
  try {
    localStorage.removeItem(LEGACY_KEY);
  } catch {
    // storage unavailable — the stale legacy key costs one wrong pre-paint at
    // most, and only on a device that cannot write anyway.
  }
}

export function initialBrand(): Brand {
  const set = document.documentElement.dataset.brand;
  return set !== undefined && isBrand(set) ? set : DEFAULT_BRAND;
}
