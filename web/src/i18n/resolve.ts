import { DEFAULT_LANGUAGE, SUPPORTED_LANGUAGES } from "./index";
import type { Language } from "./index";

// users.language (if a pack exists) → farm-locale's language subtag (if a pack
// exists) → English. A stored-but-unsupported value is silently treated as unset
// — never an error — so a language the SPA can't render degrades to the farm
// default, then English. `supported` is a parameter for testability; production
// callers pass the real SUPPORTED_LANGUAGES.
export function resolveLanguage(
  userLanguage: string | null | undefined,
  farmLocale: string | null | undefined,
  supported: readonly string[] = SUPPORTED_LANGUAGES,
): Language {
  const farmSubtag = farmLocale ? farmLocale.split("-")[0] : undefined;
  for (const candidate of [userLanguage, farmSubtag]) {
    // BCP-47 subtags are case-insensitive and stored only trimmed (not
    // canonicalised), so "ES-MX"/"En" must still match an installed "es"/"en".
    const code = candidate?.toLowerCase();
    if (code && supported.includes(code)) return code as Language;
  }
  return DEFAULT_LANGUAGE;
}
