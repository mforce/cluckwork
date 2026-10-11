import type { ReactNode } from "react";
import { Trans, useTranslation } from "react-i18next";

// The administration and setup sections of the Help guide: farm
// settings, the farm palette, the account page, the audit log,
// exports and install. Rendered by HelpPage, which owns the search,
// the contents rail and the openLink section-to-router links.
export function HelpAdminSections({ openLink }: { openLink: (sectionId: string) => ReactNode }) {
  const { t } = useTranslation("help");
  return (
    <>
      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="farm-settings">{t("farmSettingsHeading")}</h3>{openLink("farm-settings")}</div>
      <ul>
        <li>
          <Trans ns="help" i18nKey="farmSettingsIntro" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="farmSettingsTimezone" components={{ strong: <strong />, em: <em /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="farmSettingsCurrency" components={{ strong: <strong /> }} />
        </li>
        {/* #452 — preset dropdown + Custom escape hatch for date/time format overrides. */}
        <li>
          <Trans ns="help" i18nKey="farmSettingsDateTimeFormat" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="farmSettingsLogo" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="farmSettingsSquareLogo" components={{ strong: <strong /> }} />
        </li>
        {/* #179 — the post-login splash banner, independent of the logo above. */}
        <li>
          <Trans ns="help" i18nKey="farmSettingsBanner" components={{ strong: <strong /> }} />
        </li>
        {/* #444 — the farm-default Daily Entry counting unit. */}
        <li>
          <Trans ns="help" i18nKey="farmSettingsCountingUnit" components={{ strong: <strong /> }} />
        </li>
        {/* #612 — how a restricted plain Worker's sale confirmation may draw
            stock; Owner/Manager/Sales confirmations stay farm-wide (ReadOnly
            cannot confirm at all). */}
        <li>
          <Trans ns="help" i18nKey="farmSettingsWorkerSaleAllocation" components={{ strong: <strong /> }} />
        </li>
        {/* #727 — the per-farm discount ceiling. Blank and 0 are different
            settings and both are legal, which is the part people get wrong. */}
        <li>
          <Trans ns="help" i18nKey="farmSettingsMaxDiscount" components={{ strong: <strong /> }} />
        </li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="farm-palette">{t("farmPaletteHeading")}</h3>{openLink("farm-palette")}</div>
      <p>{t("farmPaletteIntro")}</p>
      <p>{t("farmPaletteLightNight")}</p>

      {/* #444 — the personal counterpart to Farm settings: what each signed-in
          person can set for themselves, whatever their role. */}
      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="account">{t("accountHeading")}</h3>{openLink("account")}</div>
      <ul>
        <li>
          <Trans ns="help" i18nKey="accountPassword" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="accountLanguage" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="accountCountingUnit" components={{ strong: <strong /> }} />
        </li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="audit">{t("auditHeading")}</h3>{openLink("audit")}</div>
      <ul>
        <li>{t("auditLog")}</li>
        <li>{t("auditRecordTypeFilter")}</li>
        <li>
          <Trans ns="help" i18nKey="auditClearingFilters" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="auditConnectedApps" components={{ strong: <strong /> }} />
        </li>
        <li>{t("auditRecordHistory")}</li>
        <li>{t("auditRecordHistoryLink")}</li>
        <li>{t("auditRecordHistorySubmit")}</li>
        <li>{t("auditRecordHistoryOlder")}</li>
        <li>{t("auditSystemActors")}</li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="export">{t("exportHeading")}</h3>{openLink("export")}</div>
      <ul>
        <li>
          <Trans ns="help" i18nKey="exportCsv" components={{ strong: <strong /> }} />
        </li>
        <li>{t("exportFormats")}</li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="install">{t("installHeading")}</h3>{openLink("install")}</div>
      <ul>
        <li>{t("installIntro")}</li>
        <li>
          <Trans ns="help" i18nKey="installSteps" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="installHttps" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="installOffline" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="installNewVersion" components={{ strong: <strong /> }} />
        </li>
      </ul>

      </section>
    </>
  );
}
