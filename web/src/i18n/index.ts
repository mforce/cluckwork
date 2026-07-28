import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import { en } from "./en";
import { es } from "./es";
import { tl } from "./tl";
import { pickInitialLanguage, readLanguageHint, writeLanguageHint } from "../lib/languageHint";

// English-first parity allowlist (#182) — see translations-status.ts for the
// full rationale. Re-exported here so both the parity test and future code can
// import it from the same place as SUPPORTED_LANGUAGES/RESOURCES.
export { TRANSLATED_NAMESPACES } from "./translations-status";
export type { TranslatedNamespace } from "./translations-status";

// Add codes here as packs ship; resolveLanguage + the language selector both
// key off this list, so a pack becomes selectable and resolvable the moment
// it is added. es/tl (#182) are machine-drafted — see the header comment in
// each pack file.
export const SUPPORTED_LANGUAGES = ["en", "es", "tl"] as const;
export type Language = (typeof SUPPORTED_LANGUAGES)[number];
export const DEFAULT_LANGUAGE: Language = "en";

// Single source of truth for the installed packs. i18n.init reads it directly,
// and catalogParity.test.ts iterates its non-en entries — so a new pack added
// here is both wired up AND parity-checked without touching either site again.
export const RESOURCES = { en, es, tl };

// Register the <html lang> + hint-persistence listener BEFORE init(), so it
// catches init()'s OWN first `languageChanged`. With in-memory resources i18next
// emits that event SYNCHRONOUSLY inside init(); a listener registered after init()
// misses it and leaves <html lang> stuck at index.html's static "en" for the whole
// pre-auth login session — an a11y/SEO defect. One listener here then also covers
// the bootstrap's resolved-language switch and every later LanguageSelector change,
// with no risk of a call site forgetting to set it.
i18n.on("languageChanged", (lng) => {
  document.documentElement.lang = lng;
  // Persist the device hint on EVERY switch — init's seed, the bootstrap's
  // resolved-language self-heal, and every LanguageSelector change — so the next
  // load (the login screen OR a signed-in refresh) opens in this language instead
  // of flashing back to English.
  writeLanguageHint(lng);
});

// Init ONCE at module load, seeding the language from the device hint (the code
// last used on this device — see lib/languageHint.ts) so a refresh or the pre-auth
// login screen (which has no `/me` to resolve from) isn't forced back to English.
// pickInitialLanguage validates against the installed packs — a stale/removed/
// garbage code degrades to English rather than erroring. i18next.init is synchronous
// with in-memory resources, so React's first paint already renders in this language
// — no flash, no pre-paint script. The authenticated bootstrap (SessionProvider)
// later calls changeLanguage with the language resolved from /me + /account, which
// OVERRIDES the hint for signed-in users (no flash — the shell is gated until that
// switch completes) and self-heals the hint to the authoritative value. fallbackLng
// "en" + returnNull:false → a missing key renders its English string (from this same
// catalog), never blank or a raw key. escapeValue:false because React already escapes
// interpolated values.
const initialLanguage = pickInitialLanguage(
  readLanguageHint(),
  SUPPORTED_LANGUAGES,
  DEFAULT_LANGUAGE,
) as Language;

void i18n.use(initReactI18next).init({
  resources: RESOURCES,
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
