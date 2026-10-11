import { SUPPORTED_LANGUAGES, loadPack } from "../i18n";

// Every installed pack, loaded through the app's own loader (#1149), so a
// language without a loader fails the tests that iterate this.
export const ALL_PACKS = Object.fromEntries(
  await Promise.all(SUPPORTED_LANGUAGES.map(async (language) => [language, await loadPack(language)] as const)),
);
