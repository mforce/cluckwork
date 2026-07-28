// Device-persistent "last UI language" hint (#182). The API (`/me.language`) is
// the source of truth once authenticated; this localStorage hint exists ONLY so
// the pre-auth LOGIN screen — which has no `/me` to resolve from — renders in the
// language the user last used ON THIS DEVICE instead of always English.
//
// After sign-in, SessionProvider's bootstrap resolves the language from
// `/me` + `/account` and overrides this, and i18next's `languageChanged` handler
// writes the resolved value straight back here — so the hint self-heals to the
// authoritative preference on every session.
//
// Device-persistent like `cluckwork.theme`: NOT cleared on logout, so a returning
// user meets their own language on the login screen. Non-sensitive — a language
// code only (validated against the installed packs at the read site, in i18n
// init, so a stale/removed code degrades to English rather than erroring).
const KEY = "cluckwork.lang";

export function readLanguageHint(): string | null {
  try {
    return localStorage.getItem(KEY);
  } catch {
    // Private mode / storage disabled — the hint is best-effort; fall through to
    // the caller's default (English).
    return null;
  }
}

export function writeLanguageHint(lng: string): void {
  try {
    localStorage.setItem(KEY, lng);
  } catch {
    // A failed write (quota, disabled storage) just means the next login falls
    // back to the resolved/English default. Nothing to recover.
  }
}

// Validate a raw hint against the installed packs, returning the language to
// initialise i18next with. Case-insensitive (BCP-47 subtags are, and a hint
// could be hand-tampered), mirroring resolve.ts. An absent / unsupported /
// garbage hint degrades to `fallback` (English) rather than erroring — so a
// removed pack or junk value can never leave i18next on an uninstalled language.
// Kept pure (packs + fallback are params) so it's unit-testable without the
// i18next singleton.
export function pickInitialLanguage(
  hint: string | null,
  supported: readonly string[],
  fallback: string,
): string {
  const code = hint?.toLowerCase();
  return code !== undefined && supported.includes(code) ? code : fallback;
}
