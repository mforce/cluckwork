import type { ReactNode } from "react";
import { Trans, useTranslation } from "react-i18next";

// The orientation sections of the Help guide: getting around, the
// dashboard, signing in, the daily loop, fixing mistakes and dialogs.
// Rendered by HelpPage, which owns the search, the contents rail and
// the openLink section-to-router links.
export function HelpGuideSections({ openLink }: { openLink: (sectionId: string) => ReactNode }) {
  const { t } = useTranslation("help");
  // The busy-button line (#236) reads the same `common` key as the
  // "Working…" announcement it explains, so the two can never drift.
  const { t: tc } = useTranslation("common");
  return (
    <>
      <section className="help-section" data-searchable="section">
          <div className="help-section-head"><h3 id="getting-around">{t("gettingAroundHeading")}</h3>{openLink("getting-around")}</div>
      <ul>
        <li>
          <Trans ns="help" i18nKey="gettingAroundSidebar" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="gettingAroundTabs" components={{ strong: <strong /> }} />
        </li>
        <li>{t("gettingAroundPageLoading")}</li>
        <li><Trans ns="help" i18nKey="gettingAroundInspector" components={{ strong: <strong /> }} /></li>
        <li>
          <Trans ns="help" i18nKey="gettingAroundErrorScreen" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="gettingAroundWhereMessagesAppear" components={{ strong: <strong /> }} />
        </li>
        <li>
          {/* #512 — the shared searchable pickers (flock/customer name fields). */}
          <Trans ns="help" i18nKey="gettingAroundSearchablePicker" components={{ strong: <strong /> }} />
        </li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="dashboard">{t("dashboardHeading")}</h3>{openLink("dashboard")}</div>
      <ul>
        <li>
          <Trans ns="help" i18nKey="dashboardTiles" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="dashboardTrend" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="dashboardStock" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="dashboardOrders" components={{ strong: <strong /> }} />
        </li>
      </ul>
      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="signing-in">{t("signingInHeading")}</h3>{openLink("signing-in")}</div>
      <ul>
        <li>
          <Trans ns="help" i18nKey="signingInBasic" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="signingInFirstRun" components={{ strong: <strong />, em: <em /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="signingInRateLimit" components={{ strong: <strong />, em: <em /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="signingInAccountLock" components={{ strong: <strong />, em: <em /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="signingInPersistence" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="signingInMultiTabResync" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="signingInStepUp" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans
            ns="help"
            i18nKey="signingInAppAddress"
            values={{ address: `${window.location.origin}/mcp` }}
            components={{ strong: <strong />, code: <code /> }}
          />
        </li>
        <li>
          <Trans ns="help" i18nKey="signingInConnectApp" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="signingInConnectedApps" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="signingInAppTools" components={{ strong: <strong />, code: <code /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="signingInAppLimits" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="signingInAppRefused" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="signingInCredentialEpoch" components={{ strong: <strong /> }} />
        </li>
      </ul>

      <p>
        <Trans ns="help" i18nKey="interfaceLanguage" components={{ strong: <strong /> }} />
      </p>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="daily-loop">{t("dailyLoopHeading")}</h3>{openLink("daily-loop")}</div>
      <p>
        <Trans ns="help" i18nKey="dailyLoopChain" components={{ strong: <strong /> }} />
      </p>
      <p className="muted">{t("dailyLoopSummary")}</p>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="mistakes">{t("mistakesHeading")}</h3>{openLink("mistakes")}</div>
      <p className="muted">
        <Trans ns="help" i18nKey="mistakesIntro" components={{ em: <em /> }} />
      </p>
      <dl className="mistakes">
        
          <div className="mistake">
            <dt>{t("mistakesRow1Mistake")}</dt>
            <dd><Trans ns="help" i18nKey="mistakesRow1Fix" components={{ strong: <strong /> }} /></dd>
          </div>
          <div className="mistake">
            <dt>{t("mistakesRow2Mistake")}</dt>
            <dd><Trans ns="help" i18nKey="mistakesRow2Fix" components={{ strong: <strong /> }} /></dd>
          </div>
          <div className="mistake">
            <dt>{t("mistakesRow3Mistake")}</dt>
            <dd><Trans ns="help" i18nKey="mistakesRow3Fix" components={{ strong: <strong /> }} /></dd>
          </div>
          <div className="mistake">
            <dt>{t("mistakesRow4Mistake")}</dt>
            <dd><Trans ns="help" i18nKey="mistakesRow4Fix" components={{ strong: <strong /> }} /></dd>
          </div>
          <div className="mistake">
            <dt><Trans ns="help" i18nKey="mistakesRow5Mistake" components={{ em: <em /> }} /></dt>
            <dd><Trans ns="help" i18nKey="mistakesRow5Fix" components={{ strong: <strong /> }} /></dd>
          </div>
          <div className="mistake">
            <dt>{t("mistakesRow6Mistake")}</dt>
            <dd>{t("mistakesRow6Fix")}</dd>
          </div>
          <div className="mistake">
            <dt>{t("mistakesRow7Mistake")}</dt>
            <dd><Trans ns="help" i18nKey="mistakesRow7Fix" components={{ strong: <strong /> }} /></dd>
          </div>
          <div className="mistake">
            <dt><Trans ns="help" i18nKey="mistakesRow8Mistake" components={{ em: <em /> }} /></dt>
            <dd><Trans ns="help" i18nKey="mistakesRow8Fix" components={{ strong: <strong /> }} /></dd>
          </div>
          <div className="mistake">
            <dt><Trans ns="help" i18nKey="mistakesRow9Mistake" components={{ em: <em /> }} /></dt>
            <dd><Trans ns="help" i18nKey="mistakesRow9Fix" components={{ strong: <strong /> }} /></dd>
          </div>
          <div className="mistake">
            <dt><Trans ns="help" i18nKey="mistakesRow10Mistake" components={{ em: <em /> }} /></dt>
            <dd><Trans ns="help" i18nKey="mistakesRow10Fix" components={{ strong: <strong /> }} /></dd>
          </div>
        
      </dl>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="dialogs">{t("dialogsHeading")}</h3>{openLink("dialogs")}</div>
      <ul>
        <li>
          <Trans ns="help" i18nKey="dialogsPopup" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="dialogsDrillDowns" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="dialogsCancel" components={{ strong: <strong /> }} />
        </li>
        <li>
          {/* #482 — the page behind a popup is now genuinely out of reach, for
              the pointer and for a screen reader alike. */}
          <Trans ns="help" i18nKey="dialogsModal" components={{ strong: <strong /> }} />
        </li>
        <li>
          {/* #236 — the pending-save indicator, in the section that already
              explains save/retry behaviour. */}
          {tc("workingHint")}
        </li>
        <li>
          <Trans ns="help" i18nKey="dialogsInlineForms" components={{ strong: <strong /> }} />
        </li>
        <li>
          {/* #250 — the −/+ steppers rolled out beyond daily entry. */}
          <Trans ns="help" i18nKey="dialogsSteppers" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="dialogsConfirm" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="dialogsVoidReason" components={{ strong: <strong /> }} />
        </li>
      </ul>

      </section>
    </>
  );
}
