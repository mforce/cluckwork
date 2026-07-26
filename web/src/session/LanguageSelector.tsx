import { useTranslation } from "react-i18next";
import { useMe } from "./SessionContext";
import { putMeLanguage } from "../api/cluckwork";
import i18n, { SUPPORTED_LANGUAGES } from "../i18n";

// English names of each installed language, keyed by code. Extend as packs ship.
const LANGUAGE_NAMES: Record<string, string> = { en: "English" };

// Per-user UI language (#182). Hidden while only one language is installed — a
// single-option picker is noise — so today it renders nothing. The change path
// persists to the server (PUT /me/language) AND switches i18next live.
export function LanguageSelector() {
  const { t } = useTranslation("account");
  const me = useMe();

  if (SUPPORTED_LANGUAGES.length <= 1) return null;

  const current = me?.language ?? i18n.language;
  const onChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
    const lang = e.target.value;
    void putMeLanguage(lang);
    void i18n.changeLanguage(lang);
  };

  return (
    <label className="field">
      <span>{t("language")}</span>
      <select value={current} onChange={onChange}>
        {SUPPORTED_LANGUAGES.map((code) => (
          <option key={code} value={code}>{LANGUAGE_NAMES[code] ?? code}</option>
        ))}
      </select>
    </label>
  );
}
