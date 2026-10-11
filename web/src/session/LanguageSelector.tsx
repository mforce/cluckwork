import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button } from "@mui/material";
import { putMeLanguage } from "../api/cluckwork";
import { usePendingAction } from "../components/usePendingAction";
import i18n, { SUPPORTED_LANGUAGES } from "../i18n";

// Names of each installed language, keyed by code, in that language's own
// script (not translated). Extend as packs ship.
const LANGUAGE_NAMES: Record<string, string> = { en: "English", es: "Español", tl: "Tagalog" };

// #1149 — why the last switch did not happen, keyed to its message.
const NOTICE_TEXT = {
  offline: "languageNeedsConnection",
  reload: "languageSavedReload",
  saveFailed: "languageSaveFailed",
} as const;

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
  // `reload` is set only after the choice is saved; the page reloads only when
  // the user presses the button.
  const [notice, setNotice] = useState<{ kind: keyof typeof NOTICE_TEXT; language: string } | null>(null);

  if (SUPPORTED_LANGUAGES.length <= 1) return null;

  // useTranslation() re-renders this component on i18next's languageChanged
  // event, so i18n.resolvedLanguage is always current — both at bootstrap
  // (seeded from the resolved user/farm language) and after a switch. Reading
  // me?.language instead would go stale the moment changeLanguage runs, since
  // MeContext is never updated to match. Resolved, not requested (#1149): after
  // a pack fails to load, the screen is in English and the picker says so.
  const current = i18n.resolvedLanguage ?? i18n.language;
  const switchTo = (lang: string) => {
    void run("language", async () => {
      // #1149 — load the pack before switching. i18next switches even when the
      // load fails, which would show English under a non-English selection, so
      // an offline first switch keeps the current language and says why.
      await i18n.loadLanguages(lang);
      if (!i18n.hasResourceBundle(lang, "common")) {
        // A failed pack stays failed in this document: i18next keeps its load
        // state and Chromium keeps the failed import, so a retry never refetches.
        // Online, save the choice and offer a reload; the next document fetches
        // the pack. Never reload unasked: the Account page may hold a draft.
        if (navigator.onLine) {
          try {
            await putMeLanguage(lang);
          } catch (err) {
            // Unsaved, a reload would bootstrap back into the old language.
            console.warn("Failed to persist language preference", err);
            setNotice({ kind: "saveFailed", language: lang });
            return;
          }
          setNotice({ kind: "reload", language: lang });
          return;
        }
        setNotice({ kind: "offline", language: lang });
        return;
      }
      setNotice(null);
      await i18n.changeLanguage(lang);
      // Optimistic: the UI already switched; the preference reconciles from /me
      // on next bootstrap. Log rather than revert (reverting mid-session is jarring).
      await putMeLanguage(lang).catch((err: unknown) => {
        console.warn("Failed to persist language preference", err);
      });
    });
  };
  const onChange = (e: React.ChangeEvent<HTMLSelectElement>) => switchTo(e.target.value);

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
      {notice !== null && (
        <p role="status" className="muted">
          {t(NOTICE_TEXT[notice.kind], { language: LANGUAGE_NAMES[notice.language] ?? notice.language })}
        </p>
      )}
      {notice?.kind === "reload" && (
        <Button variant="outlined" size="small" onClick={() => globalThis.location.reload()}>
          {t("languageReload")}
        </Button>
      )}
      {notice?.kind === "saveFailed" && (
        <Button variant="outlined" size="small" disabled={busy} onClick={() => switchTo(notice.language)}>
          {t("languageTryAgain")}
        </Button>
      )}
    </>
  );
}
