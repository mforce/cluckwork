import { useEffect, useState } from "react";
import { Trans, useTranslation } from "react-i18next";

// F18 (#71): in-app user guide + glossary. #52 restyled it into a docs layout
// with a sticky contents rail that scroll-spies the section in view. KEEP THIS
// PAGE CURRENT: the docs-sync rule (AGENTS.md) requires every user-visible
// change to update the relevant section here and specs/product/GLOSSARY.md in
// the same PR.

// Must mirror the <h3 id=...> sections below, in document order — a section
// missing here is invisible to anyone who navigates by the contents list.
//
// Task 32 (B6a, #182): the label (2nd element) is now a `help` catalog key,
// not literal text — rendered via t(label) at render time. The `id` (1st
// element) stays byte-identical: it drives the <h3 id=...> anchors below AND
// the scroll-spy IntersectionObserver in the effect further down. Do NOT
// rename an id or reorder an entry — that would break both.
const TOC = [
  ["getting-around", "tocGettingAround"],
  ["signing-in", "tocSigningIn"],
  ["daily-loop", "tocDailyLoop"],
  ["roles", "tocRoles"],
  ["dialogs", "tocDialogs"],
  ["daily-entry", "tocDailyEntry"],
  ["flocks", "tocFlocks"],
  ["grades", "tocGrades"],
  ["products", "tocProducts"],
  ["stock", "tocStock"],
  ["inventory", "tocInventory"],
  ["feed", "tocFeed"],
  ["water", "tocWater"],
  ["sales", "tocSales"],
  ["reports", "tocReports"],
  ["expenses", "tocExpenses"],
  ["history", "tocHistory"],
  ["audit", "tocAudit"],
  ["export", "tocExport"],
  ["farm-settings", "tocFarmSettings"],
  ["farm-palette", "tocFarmPalette"],
  ["account", "tocAccount"],
  ["install", "tocInstall"],
  ["mistakes", "tocMistakes"],
  ["glossary", "tocGlossary"],
] as const;

export function HelpPage() {
  const { t } = useTranslation("help");
  // The busy-button line (#236) reads the same `common` key as the
  // "Working…" announcement it explains, so the two can never drift.
  const { t: tc } = useTranslation("common");

  // Scroll-spy the contents rail: highlight the section currently in view.
  const [activeId, setActiveId] = useState<string>(TOC[0][0]);

  useEffect(() => {
    // jsdom (tests) has no IntersectionObserver — the rail still works as plain
    // anchors, it just doesn't auto-highlight there.
    if (typeof IntersectionObserver === "undefined") return;
    const observer = new IntersectionObserver(
      (entries) => {
        const inView = entries
          .filter((e) => e.isIntersecting)
          .sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top);
        if (inView[0]) setActiveId(inView[0].target.id);
      },
      // "active" once a heading reaches the top ~30% of the viewport
      { rootMargin: "0px 0px -70% 0px", threshold: 0 },
    );
    for (const [id] of TOC) {
      const el = document.getElementById(id);
      if (el) observer.observe(el);
    }
    return () => observer.disconnect();
  }, []);

  return (
    <section className="help">
      <div className="help-head">
        <p className="eyebrow">{t("eyebrow")}</p>
        <h2>{t("heading")}</h2>
        <p className="help-lead">{t("lead")}</p>
      </div>

      <div className="help-layout">
        <nav className="help-toc" aria-label={t("contentsAriaLabel")}>
          <p className="eyebrow">{t("contentsEyebrow")}</p>
          <ul>
            {TOC.map(([id, labelKey]) => (
              <li key={id}>
                <a
                  href={`#${id}`}
                  className={activeId === id ? "active" : undefined}
                  aria-current={activeId === id ? "location" : undefined}
                  onClick={() => setActiveId(id)}
                >
                  {t(labelKey)}
                </a>
              </li>
            ))}
          </ul>
        </nav>

        <div className="help-body">
          <h3 id="getting-around">{t("gettingAroundHeading")}</h3>
      <ul>
        <li>
          <Trans ns="help" i18nKey="gettingAroundSidebar" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="gettingAroundTabs" components={{ strong: <strong /> }} />
        </li>
        <li>{t("gettingAroundPageLoading")}</li>
        <li>
          <Trans ns="help" i18nKey="gettingAroundErrorScreen" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="gettingAroundWhereMessagesAppear" components={{ strong: <strong /> }} />
        </li>
      </ul>

      <h3 id="signing-in">{t("signingInHeading")}</h3>
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
          <Trans ns="help" i18nKey="signingInCredentialEpoch" components={{ strong: <strong /> }} />
        </li>
      </ul>

      <p>
        <Trans ns="help" i18nKey="interfaceLanguage" components={{ strong: <strong /> }} />
      </p>

      <h3 id="daily-loop">{t("dailyLoopHeading")}</h3>
      <p>
        <Trans ns="help" i18nKey="dailyLoopChain" components={{ strong: <strong /> }} />
      </p>
      <p className="muted">{t("dailyLoopSummary")}</p>

      <h3 id="roles">{t("rolesHeading")}</h3>
      <ul>
        <li>
          <Trans ns="help" i18nKey="rolesWorkers" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="rolesManagers" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="rolesSalesReadOnly" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="rolesAdmin" components={{ strong: <strong /> }} />
        </li>
        <li>
          {/* #356 — disable/re-enable a colleague's sign-in. */}
          <Trans ns="help" i18nKey="rolesDisableUser" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="rolesChangeEmail" components={{ strong: <strong /> }} />
        </li>
      </ul>

      <p>
        <Trans ns="help" i18nKey="ownPassword" components={{ strong: <strong />, em: <em /> }} />
      </p>

      <h3 id="dialogs">{t("dialogsHeading")}</h3>
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

      <h3 id="daily-entry">{t("dailyEntryHeading")}</h3>
      <ul>
        <li>
          <Trans ns="help" i18nKey="dailyEntryPanes" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="dailyEntryGradingDown" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="dailyEntryButtons" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="dailyEntryPutAllIn" components={{ strong: <strong /> }} />
        </li>
        <li>{t("dailyEntrySaveBar")}</li>
        <li>
          <Trans ns="help" i18nKey="dailyEntrySaveSubmit" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="dailyEntryLocking" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="dailyEntryToday" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="dailyEntryOnePerDay" components={{ strong: <strong /> }} />
        </li>
        <li>{t("dailyEntryDepletedBackfill")}</li>
      </ul>

      <h3 id="flocks">{t("flocksHeading")}</h3>
      <ul>
        <li>
          <Trans ns="help" i18nKey="flocksCurrentBirds" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="flocksLifecycle" components={{ strong: <strong /> }} />
        </li>
        <li>{t("flocksPermissions")}</li>
      </ul>

      <h3 id="grades">{t("gradesHeading")}</h3>
      <ul>
        <li>
          <Trans ns="help" i18nKey="gradesBuckets" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="gradesDeactivating" components={{ strong: <strong />, em: <em /> }} />
        </li>
        <li>{t("gradesAdminOnly")}</li>
      </ul>

      <h3 id="products">{t("productsHeading")}</h3>
      <ul>
        <li>{t("productsWhatYouSell")}</li>
        <li>
          <Trans ns="help" i18nKey="productsPackedUnits" components={{ strong: <strong /> }} />
        </li>
      </ul>

      <h3 id="stock">{t("stockHeading")}</h3>
      <ul>
        <li>
          <Trans ns="help" i18nKey="stockLots" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="stockRestricted" components={{ strong: <strong /> }} />
        </li>
        <li>{t("stockFifo")}</li>
        <li>
          <Trans ns="help" i18nKey="stockWriteOff" components={{ strong: <strong />, em: <em /> }} />
        </li>
      </ul>

      <h3 id="inventory">{t("inventoryHeading")}</h3>
      <ul>
        <li>
          <Trans ns="help" i18nKey="inventoryItems" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="inventoryPurchaseUsage" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="inventoryLedger" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="inventoryCorrections" components={{ strong: <strong />, em: <em /> }} />
        </li>
        <li>{t("inventoryPermissions")}</li>
      </ul>

      <h3 id="feed">{t("feedHeading")}</h3>
      <ul>
        <li>
          <Trans ns="help" i18nKey="feedRecording" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="feedCorrecting" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="feedDailyEntry" components={{ strong: <strong /> }} />
        </li>
      </ul>

      <h3 id="water">{t("waterHeading")}</h3>
      <ul>
        <li>
          <Trans ns="help" i18nKey="waterRecording" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="waterCorrecting" components={{ strong: <strong /> }} />
        </li>
        <li>{t("waterLifecycle")}</li>
      </ul>

      <h3 id="sales">{t("salesHeading")}</h3>
      <ul>
        <li>
          <Trans ns="help" i18nKey="salesDrafts" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="salesConfirming" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="salesVoiding" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="salesPayments" components={{ strong: <strong /> }} />
        </li>
      </ul>

      <h3 id="reports">{t("reportsHeading")}</h3>
      <ul>
        <li>
          <Trans ns="help" i18nKey="reportsProduction" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="reportsCondition" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="reportsMoney" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="reportsThrottle" components={{ strong: <strong /> }} />
        </li>
      </ul>

      <h3 id="expenses">{t("expensesHeading")}</h3>
      <ul>
        <li>{t("expensesRecording")}</li>
        <li>
          <Trans ns="help" i18nKey="expensesCorrections" components={{ strong: <strong /> }} />
        </li>
        <li>{t("expensesAdminOnly")}</li>
      </ul>

      <h3 id="history">{t("historyHeading")}</h3>
      <ul>
        <li>{t("historyBrowse")}</li>
        <li>
          <Trans ns="help" i18nKey="historyAdminActions" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="historyDraftEdit" components={{ strong: <strong /> }} />
        </li>
      </ul>

      <h3 id="audit">{t("auditHeading")}</h3>
      <ul>
        <li>{t("auditLog")}</li>
        <li>{t("auditRecordTypeFilter")}</li>
        <li>{t("auditRecordHistory")}</li>
        <li>{t("auditRecordHistoryLink")}</li>
        <li>{t("auditRecordHistorySubmit")}</li>
        <li>{t("auditRecordHistoryOlder")}</li>
        <li>{t("auditSystemActors")}</li>
      </ul>

      <h3 id="export">{t("exportHeading")}</h3>
      <ul>
        <li>
          <Trans ns="help" i18nKey="exportCsv" components={{ strong: <strong /> }} />
        </li>
        <li>{t("exportFormats")}</li>
      </ul>

      <h3 id="farm-settings">{t("farmSettingsHeading")}</h3>
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
      </ul>

      <h3 id="farm-palette">{t("farmPaletteHeading")}</h3>
      <p>{t("farmPaletteIntro")}</p>
      <p>{t("farmPaletteLightNight")}</p>

      {/* #444 — the personal counterpart to Farm settings: what each signed-in
          person can set for themselves, whatever their role. */}
      <h3 id="account">{t("accountHeading")}</h3>
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

      <h3 id="install">{t("installHeading")}</h3>
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

      <h3 id="mistakes">{t("mistakesHeading")}</h3>
      <p className="muted">
        <Trans ns="help" i18nKey="mistakesIntro" components={{ em: <em /> }} />
      </p>
      <table className="data">
        <thead>
          <tr><th>{t("mistakesTableMistakeHeader")}</th><th>{t("mistakesTableFixHeader")}</th></tr>
        </thead>
        <tbody>
          <tr>
            <td>{t("mistakesRow1Mistake")}</td>
            <td><Trans ns="help" i18nKey="mistakesRow1Fix" components={{ strong: <strong /> }} /></td>
          </tr>
          <tr>
            <td>{t("mistakesRow2Mistake")}</td>
            <td><Trans ns="help" i18nKey="mistakesRow2Fix" components={{ strong: <strong /> }} /></td>
          </tr>
          <tr>
            <td>{t("mistakesRow3Mistake")}</td>
            <td>
              <Trans ns="help" i18nKey="mistakesRow3Fix" components={{ strong: <strong /> }} />
            </td>
          </tr>
          <tr>
            <td>{t("mistakesRow4Mistake")}</td>
            <td>
              <Trans ns="help" i18nKey="mistakesRow4Fix" components={{ strong: <strong /> }} />
            </td>
          </tr>
          <tr>
            <td><Trans ns="help" i18nKey="mistakesRow5Mistake" components={{ em: <em /> }} /></td>
            <td>
              <Trans ns="help" i18nKey="mistakesRow5Fix" components={{ strong: <strong /> }} />
            </td>
          </tr>
          <tr>
            <td>{t("mistakesRow6Mistake")}</td>
            <td>{t("mistakesRow6Fix")}</td>
          </tr>
          <tr>
            <td>{t("mistakesRow7Mistake")}</td>
            <td>
              <Trans ns="help" i18nKey="mistakesRow7Fix" components={{ strong: <strong /> }} />
            </td>
          </tr>
          <tr>
            <td><Trans ns="help" i18nKey="mistakesRow8Mistake" components={{ em: <em /> }} /></td>
            <td>
              <Trans ns="help" i18nKey="mistakesRow8Fix" components={{ strong: <strong /> }} />
            </td>
          </tr>
          <tr>
            <td><Trans ns="help" i18nKey="mistakesRow9Mistake" components={{ em: <em /> }} /></td>
            <td>
              <Trans ns="help" i18nKey="mistakesRow9Fix" components={{ strong: <strong /> }} />
            </td>
          </tr>
          <tr>
            <td><Trans ns="help" i18nKey="mistakesRow10Mistake" components={{ em: <em /> }} /></td>
            <td>
              <Trans ns="help" i18nKey="mistakesRow10Fix" components={{ strong: <strong /> }} />
            </td>
          </tr>
        </tbody>
      </table>

      <h3 id="glossary">{t("glossaryHeading")}</h3>
      <table className="data">
        <tbody>
          <tr><th scope="row">{t("glossaryNavigationTerm")}</th>
            <td>{t("glossaryNavigationDef")}</td></tr>
          <tr><th scope="row">{t("glossaryPageLoadingTerm")}</th>
            <td>{t("glossaryPageLoadingDef")}</td></tr>
          <tr><th scope="row">{t("glossaryOperationalDayTerm")}</th>
            <td>{t("glossaryOperationalDayDef")}</td></tr>
          <tr><th scope="row">{t("glossaryInstallToHomeScreenTerm")}</th>
            <td><Trans ns="help" i18nKey="glossaryInstallToHomeScreenDef" components={{ strong: <strong /> }} /></td></tr>
          <tr><th scope="row">{t("glossaryNewVersionReadyTerm")}</th>
            <td>{t("glossaryNewVersionReadyDef")}</td></tr>
          {/* #532 — the login screen now asks for it before the email. */}
          <tr><th scope="row">{t("glossaryFarmCodeTerm")}</th>
            <td><Trans ns="help" i18nKey="glossaryFarmCodeDef" components={{ strong: <strong /> }} /></td></tr>
          <tr><th scope="row">{t("glossaryLoginEmailTerm")}</th>
            <td>{t("glossaryLoginEmailDef")}</td></tr>
          <tr><th scope="row">{t("glossaryFarmProvisioningTerm")}</th>
            <td>{t("glossaryFarmProvisioningDef")}</td></tr>
          <tr><th scope="row">{t("glossaryTooManySignInAttemptsTerm")}</th>
            <td>{t("glossaryTooManySignInAttemptsDef")}</td></tr>
          <tr><th scope="row">{t("glossaryForcedReauthTerm")}</th>
            <td><Trans ns="help" i18nKey="glossaryForcedReauthDef" components={{ strong: <strong /> }} /></td></tr>
          <tr><th scope="row">{t("glossaryTooManyReportsTerm")}</th>
            <td>{t("glossaryTooManyReportsDef")}</td></tr>
          <tr><th scope="row">{t("glossaryStepUpAuthTerm")}</th>
            <td>{t("glossaryStepUpAuthDef")}</td></tr>
          <tr><th scope="row">{t("glossarySomethingWentWrongScreenTerm")}</th>
            <td>{t("glossarySomethingWentWrongScreenDef")}</td></tr>
          <tr><th scope="row">{t("glossaryDailyEntryTerm")}</th>
            <td>{t("glossaryDailyEntryDef")}</td></tr>
          <tr><th scope="row">{t("glossaryEggLotTerm")}</th>
            <td>{t("glossaryEggLotDef")}</td></tr>
          <tr><th scope="row">{t("glossaryGradeTerm")}</th>
            <td>{t("glossaryGradeDef")}</td></tr>
          <tr><th scope="row">{t("glossaryEggMovementLedgerTerm")}</th>
            <td>{t("glossaryEggMovementLedgerDef")}</td></tr>
          <tr><th scope="row">{t("glossaryStockWriteOffTerm")}</th>
            <td>{t("glossaryStockWriteOffDef")}</td></tr>
          <tr><th scope="row">{t("glossaryFifoTerm")}</th>
            <td>{t("glossaryFifoDef")}</td></tr>
          <tr><th scope="row">{t("glossaryWorkerSaleAllocationTerm")}</th>
            <td>{t("glossaryWorkerSaleAllocationDef")}</td></tr>
          <tr><th scope="row">{t("glossaryCullTerm")}</th>
            <td>{t("glossaryCullDef")}</td></tr>
          <tr><th scope="row">{t("glossaryMortalityTerm")}</th>
            <td>{t("glossaryMortalityDef")}</td></tr>
          <tr><th scope="row">{t("glossaryDepleteTerm")}</th>
            <td>{t("glossaryDepleteDef")}</td></tr>
          <tr><th scope="row">{t("glossaryArchiveTerm")}</th>
            <td>{t("glossaryArchiveDef")}</td></tr>
          <tr><th scope="row">{t("glossaryWithdrawalRestrictionTerm")}</th>
            <td>{t("glossaryWithdrawalRestrictionDef")}</td></tr>
          <tr><th scope="row">{t("glossaryProductTerm")}</th>
            <td>{t("glossaryProductDef")}</td></tr>
          <tr><th scope="row">{t("glossaryPackedUnitTerm")}</th>
            <td>{t("glossaryPackedUnitDef")}</td></tr>
          {/* #444 — beside the packed unit it counts by. */}
          <tr><th scope="row">{t("glossaryCountingUnitTerm")}</th>
            <td>{t("glossaryCountingUnitDef")}</td></tr>
          <tr><th scope="row">{t("glossarySalesLineTerm")}</th>
            <td>{t("glossarySalesLineDef")}</td></tr>
          <tr><th scope="row">{t("glossaryConfirmOrderTerm")}</th>
            <td>{t("glossaryConfirmOrderDef")}</td></tr>
          <tr><th scope="row">{t("glossaryVoidOrderTerm")}</th>
            <td>{t("glossaryVoidOrderDef")}</td></tr>
          <tr><th scope="row">{t("glossaryCancelOrderTerm")}</th>
            <td>{t("glossaryCancelOrderDef")}</td></tr>
          <tr><th scope="row">{t("glossaryInventoryItemTerm")}</th>
            <td>{t("glossaryInventoryItemDef")}</td></tr>
          <tr><th scope="row">{t("glossaryInventoryLotTerm")}</th>
            <td>{t("glossaryInventoryLotDef")}</td></tr>
          <tr><th scope="row">{t("glossaryInventoryMovementLedgerTerm")}</th>
            <td>{t("glossaryInventoryMovementLedgerDef")}</td></tr>
          <tr><th scope="row">{t("glossaryWaterUsageTerm")}</th>
            <td>{t("glossaryWaterUsageDef")}</td></tr>
          <tr><th scope="row">{t("glossaryFeedUsageTerm")}</th>
            <td>{t("glossaryFeedUsageDef")}</td></tr>
          <tr><th scope="row">{t("glossaryAdjustmentDiscardTerm")}</th>
            <td>{t("glossaryAdjustmentDiscardDef")}</td></tr>
          <tr><th scope="row">{t("glossaryRolesTerm")}</th>
            <td>{t("glossaryRolesDef")}</td></tr>
          <tr>
            <th scope="row">{t("glossaryFlockScopingTerm")}</th>
            <td>{t("glossaryFlockScopingDef")}</td>
          </tr>
          <tr><th scope="row">{t("glossaryLockedEntryTerm")}</th>
            <td>{t("glossaryLockedEntryDef")}</td></tr>
          <tr><th scope="row">{t("glossaryAdjustEntryTerm")}</th>
            <td>{t("glossaryAdjustEntryDef")}</td></tr>
          <tr><th scope="row">{t("glossaryVoidEntryTerm")}</th>
            <td>{t("glossaryVoidEntryDef")}</td></tr>
          <tr><th scope="row">{t("glossaryFarmSettingsTerm")}</th>
            <td>{t("glossaryFarmSettingsDef")}</td></tr>
          <tr><th scope="row">{t("glossaryCurrencyLockTerm")}</th>
            <td>{t("glossaryCurrencyLockDef")}</td></tr>
          <tr><th scope="row">{t("glossaryFarmLogoTerm")}</th>
            <td>{t("glossaryFarmLogoDef")}</td></tr>
          <tr><th scope="row">{t("glossaryFarmBannerTerm")}</th>
            <td>{t("glossaryFarmBannerDef")}</td></tr>
          <tr>
            <th scope="row">{t("glossaryFarmPaletteTerm")}</th>
            <td>{t("glossaryFarmPaletteDef")}</td>
          </tr>
          <tr><th scope="row">{t("glossaryUiLanguageTerm")}</th>
            <td>{t("glossaryUiLanguageDef")}</td></tr>
          {/* #356 — appended last so it doesn't reshuffle the rows above. */}
          <tr><th scope="row">{t("glossaryDisabledUserTerm")}</th>
            <td>{t("glossaryDisabledUserDef")}</td></tr>
        </tbody>
      </table>

          <p className="muted">
            <Trans ns="help" i18nKey="glossaryRepoNote" components={{ code: <code /> }} />
          </p>
        </div>
      </div>
    </section>
  );
}
