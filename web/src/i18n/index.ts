import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import { en } from "./en";
import { es } from "./es";
import { tl } from "./tl";

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

// Init ONCE at module load with English resources and lng "en". The authenticated
// bootstrap (SessionProvider) later calls changeLanguage with the resolved
// language BEFORE the shell renders — never here, so there is no module-load race
// and no English→resolved flash. fallbackLng "en" + returnNull:false → a missing
// key renders its English string (from this same catalog), never blank or a raw
// key. escapeValue:false because React already escapes interpolated values.
void i18n.use(initReactI18next).init({
  resources: RESOURCES,
  lng: DEFAULT_LANGUAGE,
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
