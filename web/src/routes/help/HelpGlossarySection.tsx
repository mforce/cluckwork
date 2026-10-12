import type { ReactNode } from "react";
import { Trans, useTranslation } from "react-i18next";
import { GLOSSARY, GLOSSARY_GROUPS } from "../helpGlossary";

// The deep-linkable glossary section of the Help guide (#71, #657).
// Its data lives in ./helpGlossary.ts; `query` hides the jump rail
// while a search is in progress. Rendered by HelpPage, which owns
// the search and the openLink section-to-router links.
export function HelpGlossarySection({ openLink, query }: { openLink: (sectionId: string) => ReactNode; query: string }) {
  const { t, i18n } = useTranslation("help");
  return (
    <>
      <section className="help-section" data-searchable="glossary">
      <div className="help-section-head"><h3 id="glossary">{t("glossaryHeading")}</h3>{openLink("glossary")}</div>
      {/* Hidden during a search: a folded group has no heading to jump to. */}
      <nav className="glossary-jump" aria-label={t("glossaryJumpAriaLabel")} hidden={query !== ""}>
        <ul>
          {GLOSSARY_GROUPS.map((group) => (
            <li key={group.key}><a href={`#glossary-group-${group.key}`}>{t(group.labelKey)}</a></li>
          ))}
        </ul>
      </nav>
      {GLOSSARY_GROUPS.map((group) => {
        const entries = GLOSSARY
          .filter((entry) => entry.group === group.key)
          .map((entry) => ({ ...entry, term: t(entry.termKey) }))
          .sort((a, b) => a.term.localeCompare(b.term, i18n.resolvedLanguage));
        return (
          <div key={group.key} className="glossary-group">
            <h4 id={`glossary-group-${group.key}`}>{t(group.labelKey)}</h4>
            <div className="glossary">
              {entries.map((entry) => (
                <details key={entry.key} id={entry.id} className="glossary-entry" data-searchable="term">
                  <summary>{entry.term}</summary>
                  <p>
                    {entry.rich
                      ? <Trans ns="help" i18nKey={entry.defKey} components={{ strong: <strong /> }} />
                      : t(entry.defKey)}
                  </p>
                </details>
              ))}
            </div>
          </div>
        );
      })}

          <p className="muted">
            <Trans ns="help" i18nKey="glossaryRepoNote" components={{ code: <code /> }} />
          </p>
      </section>
    </>
  );
}
