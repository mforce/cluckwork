// #833 — pre-authentication banner caching, owner decision 2026-09-19: reuse
// the device's cached banner from the last session, the same #586 mechanism
// lib/brand.ts uses for the palette. `/account/banner` stays authenticated —
// nothing here fetches it; this module only caches bytes ALREADY fetched by
// the post-login splash (BrandSplash.tsx) and reads them back before auth.
//
// Stored as real bytes in IndexedDB, not base64 text in localStorage:
// production's CSP is `img-src 'self' blob:` (SecurityHeaders.cs) — no
// `data:` — so an <img src="data:..."> is refused there even though it
// paints fine under Vite dev (no CSP) or a jsdom test (no CSP at all). A
// blob: object URL, reconstructed from the stored bytes, is what
// useLogoObjectUrl.ts's own authenticated logo/banner fetch already uses;
// this mirrors it for the cached, pre-authentication path. Bytes also avoid
// base64's ~33% size expansion against localStorage's quota — moot once the
// storage moved regardless, but the ceiling below still matters.
//
// Each entry carries the account id alongside the blob, not just the slug:
// a farm code is reusable after a rename (#732's retired-code cost), so the
// slug alone cannot tell "still farm A" from "now farm B, reusing A's old
// code". Login.tsx never trusts a cached entry across that boundary —
// clearBannerIfWrongAccount reconciles it the moment a sign-in proves which
// account a code now belongs to.
import { useEffect, useState } from "react";
import { farmBindingToken, getBoundAccountId, getBoundFarmCode } from "../auth/tokenStore";

const DB_NAME = "cluckwork-banners";
const STORE_NAME = "banners";
const DB_VERSION = 1;

// Matches ImageSanitizer.MaxBannerByteLengthCeiling (Cluckwork.Domain/Media)
// — the hard upper bound ANY deployment's FarmBanner:MaxUploadBytes can be
// configured to, not this farm's own current cap (Settings never hands this
// module that number). A blob fetched from /account/banner already cleared
// whatever cap was live at upload time; this is a defensive ceiling against
// a future caller handing this function something larger, not a limit this
// path expects to hit.
const MAX_BANNER_BYTES = 15 * 1024 * 1024;

// Bytes + MIME type, not a raw Blob: IndexedDB's structured-clone support
// for Blob directly is inconsistent across browsers/polyfills (and, in this
// project's own test environment, a documented jsdom cross-realm gap —
// jsdom/jsdom#3363 — silently corrupts a stored Blob into "[object Object]"
// through fake-indexeddb's clone fallback). An ArrayBuffer is a primitive
// every structured-clone implementation handles correctly, and Blob's own
// `.arrayBuffer()`/reconstruction round-trip costs nothing extra.
interface BannerRecord {
  accountId: string;
  bytes: ArrayBuffer;
  type: string;
}

// Exported for anything that wants the exact storage key a slug resolves to
// (tests, mainly) — IndexedDB keys are per-database already, so this is
// currently the identity function, but callers should still go through it
// rather than assume that stays true.
export function bannerKeyFor(slug: string): string {
  return slug;
}

// Mirrors farmCodeCache.ts's normalizeFarmCode (trim + lowercase, no shape
// validation) without importing it: that module already imports THIS one
// (forgetBannerFor, below), and a two-line duplicate is cheaper than a
// circular import.
function normalize(value: string): string {
  return value.trim().toLowerCase();
}

function openDb(): Promise<IDBDatabase> {
  // Some browsers (Safari private mode, historically) expose no IndexedDB at
  // all rather than failing an open() call — checked explicitly so that path
  // rejects the same way a real failure does, and every caller's existing
  // try/catch already leaves Login on neutral branding for either one.
  if (typeof indexedDB === "undefined") return Promise.reject(new Error("IndexedDB unavailable"));
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, DB_VERSION);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE_NAME);
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error ?? new Error("IndexedDB open failed"));
  });
}

async function putBanner(slug: string, record: BannerRecord): Promise<void> {
  const db = await openDb();
  try {
    await new Promise<void>((resolve, reject) => {
      const tx = db.transaction(STORE_NAME, "readwrite");
      tx.objectStore(STORE_NAME).put(record, bannerKeyFor(slug));
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error ?? new Error("IndexedDB write failed"));
      tx.onabort = () => reject(tx.error ?? new Error("IndexedDB write aborted"));
    });
  } finally {
    db.close();
  }
}

async function getBannerRecord(slug: string): Promise<BannerRecord | null> {
  const db = await openDb();
  try {
    return await new Promise<BannerRecord | null>((resolve, reject) => {
      const tx = db.transaction(STORE_NAME, "readonly");
      const req = tx.objectStore(STORE_NAME).get(bannerKeyFor(slug));
      req.onsuccess = () => resolve((req.result as BannerRecord | undefined) ?? null);
      req.onerror = () => reject(req.error ?? new Error("IndexedDB read failed"));
    });
  } finally {
    db.close();
  }
}

async function deleteBannerRecord(slug: string): Promise<void> {
  const db = await openDb();
  try {
    await new Promise<void>((resolve, reject) => {
      const tx = db.transaction(STORE_NAME, "readwrite");
      tx.objectStore(STORE_NAME).delete(bannerKeyFor(slug));
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error ?? new Error("IndexedDB delete failed"));
    });
  } finally {
    db.close();
  }
}

// #587-style: called alongside forgetBrandFor when "Forget this farm" drops
// a roster entry, so a forgotten farm's banner stops appearing on this
// device the moment its code does. Kept synchronous at the call site (never
// awaited, never throws) — IndexedDB is inherently async, so the delete
// itself runs in the background; a caller that needs to know it landed
// should call deleteBannerRecord (unexported) directly instead.
export function forgetBannerFor(slug: string): void {
  void deleteBannerRecord(slug).catch(() => {
    // storage unavailable — a stale cached banner on a device that cannot
    // write costs one wrong pre-auth image at most, same cost brand.ts accepts.
  });
}

// Called from BrandSplash once its own banner fetch resolves.
export async function cacheBannerBytes(blob: Blob, tokenAt: string): Promise<void> {
  if (tokenAt !== farmBindingToken()) return;
  if (blob.size > MAX_BANNER_BYTES) return;
  const slug = getBoundFarmCode();
  const accountId = getBoundAccountId();
  if (slug === null || accountId === null) return;
  try {
    const bytes = await blob.arrayBuffer();
    // Re-checked after the arrayBuffer() read too: a farm switch or logout
    // mid-read must not attribute farm B's bytes to farm A's slug.
    if (tokenAt !== farmBindingToken()) return;
    await putBanner(slug, { accountId, bytes, type: blob.type });
  } catch {
    // Quota exceeded or storage unavailable — nothing more to do; Login
    // falls back to neutral branding on its next read.
    return;
  }
  // Re-checked after the async write: a farm switch or logout mid-write must
  // not leave farm B's bytes attributed to farm A's slug, or vice versa.
  if (tokenAt !== farmBindingToken()) {
    void deleteBannerRecord(slug).catch(() => {});
  }
}

// #833 findings 2/3 — called from AuthContext.login() once a sign-in
// resolves and the new account is bound. A farm code is reusable (#732), so
// a cache entry keyed by slug alone can belong to a DIFFERENT account than
// the one that just proved it owns that code today; this is what keeps a
// reused code from inheriting the previous holder's image. Until this runs
// once, a device that already held account A's banner may show it briefly
// under a code since reassigned to account B — a stale attribution to a
// device that already had the bytes, not a new disclosure to a device that
// never signed in there (recorded in GLOSSARY.md).
export async function clearBannerIfWrongAccount(slug: string, signedInAccountId: string): Promise<void> {
  try {
    const record = await getBannerRecord(slug);
    if (record !== null && record.accountId !== signedInAccountId) {
      await deleteBannerRecord(slug);
    }
  } catch {
    // storage unavailable — nothing to reconcile
  }
}

// Read on Login, before authentication, keyed to the farm-code FIELD's
// CURRENT value (#833 finding 2) — not "exactly one remembered farm", which
// kept showing farm A's banner while an operator was mid-typing farm B's
// code, and never cleared it until B was actually submitted. A value that
// matches no cached entry shows neutral branding, whatever else this device
// remembers.
export async function readCachedBannerBlob(typedFarmCode: string): Promise<Blob | null> {
  const slug = normalize(typedFarmCode);
  if (slug === "") return null;
  try {
    const record = await getBannerRecord(slug);
    return record === null ? null : new Blob([record.bytes], { type: record.type });
  } catch {
    return null;
  }
}

// Object-URL lifecycle for the hook below — same create-on-resolve,
// revoke-on-change-or-unmount shape as useLogoObjectUrl.ts's
// useImageObjectUrl, applied to a cache read instead of a fetch.
export function useCachedBannerUrl(typedFarmCode: string): string | null {
  const [url, setUrl] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    let objectUrl: string | null = null;

    void readCachedBannerBlob(typedFarmCode).then((blob) => {
      if (cancelled || blob === null) return;
      objectUrl = URL.createObjectURL(blob);
      setUrl(objectUrl);
    });

    return () => {
      cancelled = true;
      setUrl(null);
      if (objectUrl !== null) URL.revokeObjectURL(objectUrl);
    };
  }, [typedFarmCode]);

  return url;
}
