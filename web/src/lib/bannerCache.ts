// #833 — pre-authentication banner caching, owner decision 2026-09-19: reuse
// the device's cached banner from the last session, the same #586 mechanism
// lib/brand.ts uses for the palette. `/account/banner` stays authenticated —
// nothing here fetches it; this module only caches bytes ALREADY fetched by
// the post-login splash (BrandSplash.tsx) and reads them back before auth.
//
// One key per farm, same shape as brand.ts's brandKeyFor: a cache entry is
// written only under a slug this session's login typed, and only while that
// login's account is still the tab's bound account (farmBindingToken()) — a
// response that outlived a bind/unbind cycle caches nothing, same reasoning
// applyBrand gives for the palette.
import { farmBindingToken, getBoundFarmCode } from "../auth/tokenStore";

const KEY_PREFIX = "cluckwork.banner:";

export function bannerKeyFor(slug: string): string {
  return KEY_PREFIX + slug;
}

// #587-style: called alongside forgetBrandFor when "Forget this farm" drops
// a roster entry, so a forgotten farm's banner stops appearing on this
// device the moment its code does.
export function forgetBannerFor(slug: string): void {
  try {
    localStorage.removeItem(bannerKeyFor(slug));
  } catch {
    // storage unavailable — a stale cached banner on a device that cannot
    // write costs one wrong pre-auth image at most, same cost brand.ts accepts.
  }
}

function blobToDataUrl(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = () => reject(reader.error ?? new Error("FileReader failed"));
    reader.readAsDataURL(blob);
  });
}

// Called from BrandSplash once its own banner fetch resolves. localStorage is
// string-only, so the blob is read as a data URL rather than stored raw;
// production banners are capped at FarmLogo__MaxUploadBytes-scale (a few MB),
// well inside the quota this accepts failing gracefully on.
export async function cacheBannerBytes(blob: Blob, tokenAt: string): Promise<void> {
  if (tokenAt !== farmBindingToken()) return;
  const slug = getBoundFarmCode();
  if (slug === null) return;
  let dataUrl: string;
  try {
    dataUrl = await blobToDataUrl(blob);
  } catch {
    return; // FileReader failure — nothing to cache
  }
  // Re-checked after the async read: a farm switch or logout mid-read must
  // not cache farm B's bytes under farm A's key or vice versa.
  if (tokenAt !== farmBindingToken()) return;
  try {
    localStorage.setItem(bannerKeyFor(slug), dataUrl);
  } catch {
    // Quota exceeded — drop any half-written value rather than leave a
    // corrupt/partial entry that would render broken on the next Login visit.
    try {
      localStorage.removeItem(bannerKeyFor(slug));
    } catch {
      // storage fully unavailable; nothing more to do
    }
  }
}

// Read on Login, before authentication. Mirrors applyDeviceBrand's own rule
// (lib/brand.ts): a device can justify showing a cached image only when it
// remembers exactly ONE farm — with two or more remembered codes, which
// farm's banner belongs on screen is ambiguous, so neither shows and Login
// falls back to neutral Cluckwork branding.
export function readCachedBanner(rememberedCodes: string[]): string | null {
  if (rememberedCodes.length !== 1) return null;
  try {
    return localStorage.getItem(bannerKeyFor(rememberedCodes[0]));
  } catch {
    return null;
  }
}
