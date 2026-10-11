import i18n from "i18next";
import type { BackendModule } from "i18next";
import { initReactI18next } from "react-i18next";
import { en } from "./en";
import { pickInitialLanguage, readLanguageHint, writeLanguageHint } from "../lib/languageHint";

// English-first parity allowlist (#182) — see translations-status.ts for the
// full rationale. Re-exported here so both the parity test and future code can
// import it from the same place as SUPPORTED_LANGUAGES and loadPack.
export { TRANSLATED_NAMESPACES } from "./translations-status";
export type { TranslatedNamespace } from "./translations-status";

// Add codes here as packs ship; resolveLanguage + the language selector both
// key off this list, so a pack becomes selectable and resolvable the moment
// it is added. es/tl (#182) are machine-drafted — see the header comment in
// each pack file.
export const SUPPORTED_LANGUAGES = ["en", "es", "tl"] as const;
export type Language = (typeof SUPPORTED_LANGUAGES)[number];
export const DEFAULT_LANGUAGE: Language = "en";

// #1149 — only English ships in the bundle, because it is every other pack's
// fallback. Each other pack is its own chunk, kept out of the service worker's
// precache and cached at runtime on first use (vite.config.ts), so a device
// precaches one language instead of all three. catalogParity.test.ts loads
// every SUPPORTED_LANGUAGES pack through loadPack, so a new language without a
// loader fails there.
type Catalog = Record<string, Record<string, string>>;

export const LAZY_PACKS = {
  es: () => import("./es").then((module) => module.es),
  tl: () => import("./tl").then((module) => module.tl),
} satisfies Record<Exclude<Language, "en">, () => Promise<Catalog>>;

export function loadPack(language: Language): Promise<Catalog> {
  return language === "en" ? Promise.resolve(en) : LAZY_PACKS[language]();
}

// A stalled request would otherwise hold the first render forever (main.tsx
// waits on i18nReady); past this deadline the load counts as failed and the app
// starts in English. 10 s covers the ~55 KiB gzipped pack on a slow 3G link.
export const PACK_TIMEOUT_MS = 10_000;

// i18next calls `read` once per namespace, ten at a time, so the deadline
// belongs to the language: one bounded load that every namespace shares. A
// failed load stays failed in this document anyway (see LanguageSelector).
// `false` as the second argument tells i18next not to retry: a failed chunk
// import means offline, and LanguageSelector reports that at once instead of
// after i18next's backoff.
const boundedLoads = new Map<string, Promise<Catalog>>();

const lazyPackBackend: BackendModule = {
  type: "backend",
  init() {},
  read(language, namespace, callback) {
    if (!(language in LAZY_PACKS)) return callback(null, {});
    let load = boundedLoads.get(language);
    if (!load) {
      const deadline = new Promise<never>((_, reject) =>
        setTimeout(reject, PACK_TIMEOUT_MS, new Error(`The ${language} catalog timed out`)));
      load = Promise.race([loadPack(language as Language), deadline]);
      boundedLoads.set(language, load);
    }
    load.then(
      (pack) => callback(null, pack[namespace] ?? {}),
      (error) => callback(error, false),
    );
  },
};

// Register the <html lang> + hint-persistence listener BEFORE init(), so it
// catches init()'s OWN first `languageChanged`. With in-memory resources i18next
// emits that event SYNCHRONOUSLY inside init(); a listener registered after init()
// misses it and leaves <html lang> stuck at index.html's static "en" for the whole
// pre-auth login session — an a11y/SEO defect. One listener here then also covers
// the bootstrap's resolved-language switch and every later LanguageSelector change,
// with no risk of a call site forgetting to set it.
i18n.on("languageChanged", (lng) => {
  // #1149 — the language the text is actually in: a pack that failed to load
  // leaves English on screen under the requested language.
  document.documentElement.lang = i18n.resolvedLanguage ?? lng;
  // Persist the device hint on EVERY switch — init's seed, the bootstrap's
  // resolved-language self-heal, and every LanguageSelector change — so the next
  // load (the login screen OR a signed-in refresh) opens in this language instead
  // of flashing back to English. It keeps the REQUESTED language, so a pack that
  // failed to load is tried again on the next load.
  writeLanguageHint(lng);
});

// Init ONCE at module load, seeding the language from the device hint (the code
// last used on this device — see lib/languageHint.ts) so a refresh or the pre-auth
// login screen (which has no `/me` to resolve from) isn't forced back to English.
// pickInitialLanguage validates against the installed packs — a stale/removed/
// garbage code degrades to English rather than erroring. English initialises
// synchronously; another hinted language loads its chunk first, and main.tsx waits
// on i18nReady, so React's first paint already renders in this language. The
// authenticated bootstrap (SessionProvider) later calls changeLanguage with the
// language resolved from /me + /account, which OVERRIDES the hint for signed-in
// users (no flash — the shell is gated until that switch completes) and self-heals
// the hint to the authoritative value. fallbackLng
// "en" + returnNull:false → a missing key renders its English string (from this same
// catalog), never blank or a raw key. escapeValue:false because React already escapes
// interpolated values.
const initialLanguage = pickInitialLanguage(
  readLanguageHint(),
  SUPPORTED_LANGUAGES,
  DEFAULT_LANGUAGE,
) as Language;

// Resolves once the initial language is loaded, or has failed to load and
// falls back to English. main.tsx renders after it, so the login screen opens
// in the hinted language with no English flash.
export const i18nReady = i18n.use(lazyPackBackend).use(initReactI18next).init({
  resources: { en },
  partialBundledLanguages: true,
  lng: initialLanguage,
  fallbackLng: DEFAULT_LANGUAGE,
  defaultNS: "common",
  ns: Object.keys(en),
  interpolation: { escapeValue: false },
  // The catalog is two levels: namespace (ns, ":" separated) → FLAT key. We use
  // no dot-nesting, and the `errors` namespace's keys ARE dotted API codes
  // ("Me.Language.Format"), so disable key-path splitting — a dotted key is a
  // literal key, never a nested lookup.
  keySeparator: false,
  returnNull: false,
});

export default i18n;
