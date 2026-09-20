// Pre-auth banners use IndexedDB bytes so production CSP can render blob URLs.
// Account IDs disambiguate reusable farm codes when login confirms ownership.
import { useEffect, useState } from "react";
import { farmBindingToken, getBoundAccountId, getBoundFarmCode } from "../auth/tokenStore";

const DB_NAME = "cluckwork-banners";
const STORE_NAME = "banners";
const DB_VERSION = 1;

const MAX_BANNER_BYTES = 15 * 1024 * 1024;

interface BannerRecord {
  accountId: string;
  bytes: ArrayBuffer;
  type: string;
}

export function bannerKeyFor(slug: string): string {
  return slug;
}

function normalize(value: string): string {
  return value.trim().toLowerCase();
}

function openDb(): Promise<IDBDatabase> {
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
      tx.onabort = () => reject(tx.error ?? new Error("IndexedDB read aborted"));
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
      tx.onabort = () => reject(tx.error ?? new Error("IndexedDB delete aborted"));
    });
  } finally {
    db.close();
  }
}

export function forgetBannerFor(slug: string): void {
  void deleteBannerRecord(slug).catch(() => {
    // Best-effort device cache cleanup.
  });
}

export async function cacheBannerBytes(blob: Blob, tokenAt: string): Promise<void> {
  if (tokenAt !== farmBindingToken()) return;
  if (blob.size > MAX_BANNER_BYTES) return;
  const slug = getBoundFarmCode();
  const accountId = getBoundAccountId();
  if (slug === null || accountId === null) return;
  try {
    const bytes = await blob.arrayBuffer();
    if (tokenAt !== farmBindingToken()) return;
    await putBanner(slug, { accountId, bytes, type: blob.type });
  } catch {
    return;
  }
  if (tokenAt !== farmBindingToken()) {
    void deleteBannerRecord(slug).catch(() => {});
  }
}

export async function clearBannerIfWrongAccount(slug: string, signedInAccountId: string): Promise<void> {
  try {
    const record = await getBannerRecord(slug);
    if (record !== null && record.accountId !== signedInAccountId) {
      await deleteBannerRecord(slug);
    }
  } catch {
    // Keep authentication independent of optional device storage.
  }
}

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
