import { useTranslation } from "react-i18next";
import { putMeLanguage } from "../api/cluckwork";
import { usePendingAction } from "../components/usePendingAction";
import i18n, { SUPPORTED_LANGUAGES } from "../i18n";

// Names of each installed language, keyed by code, in that language's own
// script (not translated). Extend as packs ship.
const LANGUAGE_NAMES: Record<string, string> = { en: "English", es: "Español", tl: "Tagalog" };

// Per-user UI language (#182). Hidden while only one language is installed — a
// single-option picker is noise — so it renders only once more than one
// language is installed, which is the case today (en/es/tl). The change path
// persists to the server (PUT /me/language) AND switches i18next live.
export function LanguageSelector() {
  const { t } = useTranslation("account");
  // #236: component-local flight — the select disables while the persist is in
  // flight so a second switch cannot race the first (no spinner; a <select>
  // cannot carry one). Called before the early return: hooks are unconditional.
  const { busy, run } = usePendingAction();

  if (SUPPORTED_LANGUAGES.length <= 1) return null;

  // useTranslation() re-renders this component on i18next's languageChanged
  // event, so i18n.language is always current — both at bootstrap (seeded from
  // the resolved user/farm language) and after a switch. Reading me?.language
  // instead would go stale the moment changeLanguage runs, since MeContext is
  // never updated to match.
  const current = i18n.language;
  const onChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
    const lang = e.target.value;
    // The i18next switch stays outside the flight — it is local and instant;
    // only the server persist holds the select inert.
    void i18n.changeLanguage(lang);
    void run("language", () => putMeLanguage(lang)).catch((err) => {
      // Optimistic: the UI already switched; the preference reconciles from /me
      // on next bootstrap. Log rather than revert (reverting mid-session is jarring).
      console.warn("Failed to persist language preference", err);
    });
  };

  return (
    <label className="field">
      <span>{t("language")}</span>
      <select value={current} onChange={onChange} disabled={busy}>
        {SUPPORTED_LANGUAGES.map((code) => (
          <option key={code} value={code}>{LANGUAGE_NAMES[code] ?? code}</option>
        ))}
      </select>
    </label>
  );
}
