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
// so its cache may be absent or stale, but it can never hold another farm's colour.
import { getBoundFarmCode } from "../auth/tokenStore";

export const BRANDS = ["aubergine", "forest", "slate", "terracotta"] as const;
export type Brand = (typeof BRANDS)[number];

// Aubergine is the default AND carries no data-brand attribute, which is what
// makes an unknown id degrade to it with no validation on the CSS side.
export const DEFAULT_BRAND: Brand = "aubergine";

// #586 — one key per farm. The prefix lives here because this module owns the
// brand namespace; farmCodeCache imports brandKeyFor rather than rebuilding it.
const KEY_PREFIX = "cluckwork.brand:";

export function brandKeyFor(slug: string): string {
  return KEY_PREFIX + slug;
}

// #586 — everything this device remembers about ONE farm's palette. Used by
// "Forget this farm" (#587): the roster entry is what the login screen SHOWS,
// these keys are what it PAINTS.
//
// The un-namespaced pre-#586 key is NOT touched here: it is purged once at
// startup by purgeUnscopedAccountState(), and nothing reads it in between.
export function forgetBrandFor(slug: string): void {
  for (const key of [brandKeyFor(slug)]) {
    try {
      localStorage.removeItem(key);
    } catch {
      // storage unavailable — a stale palette on a device that cannot write
      // costs one wrong pre-paint at most.
    }
  }
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
}

export function initialBrand(): Brand {
  const set = document.documentElement.dataset.brand;
  return set !== undefined && isBrand(set) ? set : DEFAULT_BRAND;
}
