import { useState } from "react";
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
  // #236: component-local flight — the select disables while the pack loads
  // and the persist is in flight, so a second switch cannot race the first (no
  // spinner; a <select> cannot carry one). Called before the early return:
  // hooks are unconditional.
  const { busy, run } = usePendingAction();
  const [unreachable, setUnreachable] = useState<string | null>(null);

  if (SUPPORTED_LANGUAGES.length <= 1) return null;

  // useTranslation() re-renders this component on i18next's languageChanged
  // event, so i18n.resolvedLanguage is always current — both at bootstrap
  // (seeded from the resolved user/farm language) and after a switch. Reading
  // me?.language instead would go stale the moment changeLanguage runs, since
  // MeContext is never updated to match. Resolved, not requested (#1149): after
  // a pack fails to load, the screen is in English and the picker says so.
  const current = i18n.resolvedLanguage ?? i18n.language;
  const onChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
    const lang = e.target.value;
    void run("language", async () => {
      // #1149 — load the pack before switching. i18next switches even when the
      // load fails, which would show English under a non-English selection, so
      // an offline first switch keeps the current language and says why.
      await i18n.loadLanguages(lang);
      if (!i18n.hasResourceBundle(lang, "common")) {
        // A failed pack stays failed in this document: i18next keeps its load
        // state and Chromium keeps the failed import, so a retry never refetches.
        // Back online, save the choice and reload; the next document fetches it.
        if (navigator.onLine) {
          await putMeLanguage(lang).catch((err: unknown) => {
            console.warn("Failed to persist language preference", err);
          });
          globalThis.location.reload();
          return;
        }
        setUnreachable(lang);
        return;
      }
      setUnreachable(null);
      await i18n.changeLanguage(lang);
      // Optimistic: the UI already switched; the preference reconciles from /me
      // on next bootstrap. Log rather than revert (reverting mid-session is jarring).
      await putMeLanguage(lang).catch((err: unknown) => {
        console.warn("Failed to persist language preference", err);
      });
    });
  };

  // The note sits outside the <label> so it never joins the select's name.
  return (
    <>
      <label className="field">
        <span>{t("language")}</span>
        <select value={current} onChange={onChange} disabled={busy}>
          {SUPPORTED_LANGUAGES.map((code) => (
            <option key={code} value={code}>{LANGUAGE_NAMES[code] ?? code}</option>
          ))}
        </select>
      </label>
      {unreachable !== null && (
        <p role="status" className="muted">
          {t("languageNeedsConnection", { language: LANGUAGE_NAMES[unreachable] ?? unreachable })}
        </p>
      )}
    </>
  );
}
