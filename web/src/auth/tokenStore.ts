import type { TokenPair } from "../api/types";

// Token persistence. localStorage keeps the session across reloads; the access
// token is short-lived and the refresh token is single-use + rotated server-side
// (Cluckwork.Infrastructure/Identity), so this is an acceptable MVP trade-off.
// Revisit (httpOnly cookie) if XSS surface grows.
const KEY = "cluckwork.tokens";

export function loadTokens(): TokenPair | null {
  const raw = localStorage.getItem(KEY);
  if (!raw) return null;
  try {
    return JSON.parse(raw) as TokenPair;
  } catch {
    localStorage.removeItem(KEY);
    return null;
  }
}

export function saveTokens(tokens: TokenPair): void {
  localStorage.setItem(KEY, JSON.stringify(tokens));
}

export function clearTokens(): void {
  localStorage.removeItem(KEY);
}
