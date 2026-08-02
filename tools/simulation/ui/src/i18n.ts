// tools/simulation/ui/src/i18n.ts — selector text, resolved from the SPA's own
// catalogs instead of retyped as English literals.
//
// WHY NOT JUST WRITE "Sign in" IN THE SPEC. Every user-facing string in this app
// is translated (#182: en, es, tl all ship today and render app-wide). A spec
// that hardcodes English asserts against one language and silently stops
// exercising the other two — and the i18n specs, the ones whose entire job is to
// prove es/tl render, could not be written at all.
//
// WHAT THIS DOES AND DOES NOT PROVE. Reading the same catalog the app reads means
// this cannot catch a mislabelled button: change `auth:signIn` to "Log in" and
// both the app and the selector move together. That is the correct trade and not
// a gap being tolerated — the guarantee under test is *behavioural* (can this
// persona complete this flow), not editorial. What it DOES catch, and a hardcoded
// English literal could not, is the language never switching at all: assert
// `es.nav.dashboard` and a broken language resolution fails the spec, because the
// screen would still be showing the English string.
//
// IMPORT SAFETY. Only `en.ts` / `es.ts` / `tl.ts` are imported. Those three are
// pure data modules with ZERO imports of their own. `web/src/i18n/index.ts` is
// NOT importable here — it calls `i18n.init()` at module load and pulls in
// i18next + react-i18next, neither of which belongs in a Playwright process.
// Same for `enums.ts` and `resolve.ts`, which transitively import `./index`.

import { en } from "../../../../web/src/i18n/en";
import { es } from "../../../../web/src/i18n/es";
import { tl } from "../../../../web/src/i18n/tl";

/** The languages the SPA ships (web/src/i18n/index.ts SUPPORTED_LANGUAGES). */
export const LANGUAGES = ["en", "es", "tl"] as const;
export type Language = (typeof LANGUAGES)[number];

const CATALOGS: Record<Language, unknown> = { en, es, tl };

type Namespace = keyof typeof en;

/**
 * Resolve one `namespace:key` against a language's catalog.
 *
 * i18next is configured with `keySeparator: false`, so a key is flat WITHIN its
 * namespace — `t("dailyEntry:totalEggsLabel")`, never a dotted path. This mirrors
 * that exactly: split on the first `:` and index once.
 *
 * A miss THROWS rather than falling back to English. In the app, `fallbackLng`
 * degrading a missing key to its English string is correct behaviour; here it
 * would mean an es spec quietly asserting an English string and passing while
 * proving nothing — the precise failure this module exists to prevent. A missing
 * key is a broken spec, and it should say so.
 */
export function t(
  lang: Language,
  key: `${Namespace & string}:${string}`,
  vars?: Record<string, string | number>,
): string {
  const [ns, ...rest] = key.split(":");
  const leaf = rest.join(":");
  const catalog = CATALOGS[lang] as Record<string, Record<string, string> | undefined>;
  const bundle = catalog[ns!];
  if (!bundle) throw new Error(`i18n: no namespace "${ns}" in the ${lang} catalog.`);

  const value = bundle[leaf];
  if (typeof value !== "string") {
    throw new Error(
      `i18n: no key "${leaf}" in namespace "${ns}" of the ${lang} catalog. `
        + `Deliberately not falling back to English — a silent fallback here would let an `
        + `es/tl spec assert an English string and pass while proving nothing.`,
    );
  }
  return vars ? interpolate(value, vars) : value;
}

/** English shorthand — the default for behavioural specs, which run in one language. */
export function tEn(key: `${Namespace & string}:${string}`, vars?: Record<string, string | number>): string {
  return t("en", key, vars);
}

/**
 * i18next's `{{var}}` interpolation, reproduced for the handful of keys that
 * carry one. Deliberately minimal: no formatters, no nesting, no plural
 * selection. A spec that needs those is asserting on copy rather than behaviour
 * and should assert on something stabler — a heading, a role, a row count.
 */
function interpolate(template: string, vars: Record<string, string | number>): string {
  return template.replace(/\{\{\s*([\w.]+)\s*\}\}/g, (whole, name: string) => {
    const v = vars[name];
    return v === undefined ? whole : String(v);
  });
}

/**
 * The stable, language-independent PREFIX of an interpolated string — everything
 * before its first `{{…}}`.
 *
 * For matching a heading like `dailyEntry:entryLockedBanner`, whose tail is a
 * runtime value. Playwright's `getByText` does substring matching by default, so
 * the prefix is enough to find the element without the spec having to predict
 * the interpolated value.
 */
export function prefixOf(lang: Language, key: `${Namespace & string}:${string}`): string {
  const raw = t(lang, key);
  const cut = raw.indexOf("{{");
  const prefix = (cut === -1 ? raw : raw.slice(0, cut)).trim();
  if (prefix.length < 4) {
    throw new Error(
      `i18n: "${key}" in ${lang} starts with an interpolation ("${raw}"), leaving no stable `
        + `prefix to match on. Match on a neighbouring heading or a role instead.`,
    );
  }
  return prefix;
}
