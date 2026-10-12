import type { ReactNode } from "react";
import { Trans, useTranslation } from "react-i18next";

// The per-screen sections of the Help guide: daily entry, flocks,
// stock, history, sales, products, grades, reports, expenses,
// inventory, feed, water and roles. Rendered by HelpPage, which owns
// the search, the contents rail and the openLink section-to-router
// links.
export function HelpScreenSections({ openLink }: { openLink: (sectionId: string) => ReactNode }) {
  const { t } = useTranslation("help");
  return (
    <>
      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="daily-entry">{t("dailyEntryHeading")}</h3>{openLink("daily-entry")}</div>
      <ul>
        <li>
          <Trans ns="help" i18nKey="dailyEntryPanes" components={{ strong: <strong /> }} />
        </li>
        <li>
          {t("dailyEntryDefaultFlock")}
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

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="flocks">{t("flocksHeading")}</h3>{openLink("flocks")}</div>
      <ul>
        <li>
          <Trans ns="help" i18nKey="flocksCurrentBirds" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="flocksLifecycle" components={{ strong: <strong /> }} />
        </li>
        <li>{t("flocksPermissions")}</li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="stock">{t("stockHeading")}</h3>{openLink("stock")}</div>
      <ul>
        <li>
          <Trans ns="help" i18nKey="stockLots" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="stockRestricted" components={{ strong: <strong /> }} />
        </li>
        <li>{t("stockFifo")}</li>
        <li>
          <Trans ns="help" i18nKey="stockLowStockFloor" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="stockWriteOff" components={{ strong: <strong />, em: <em /> }} />
        </li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="history">{t("historyHeading")}</h3>{openLink("history")}</div>
      <ul>
        <li>{t("historyBrowse")}</li>
        <li>{t("historyReconciliation")}</li>
        <li>
          <Trans ns="help" i18nKey="historyAdminActions" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="historyDraftEdit" components={{ strong: <strong /> }} />
        </li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="sales">{t("salesHeading")}</h3>{openLink("sales")}</div>
      <ul>
        <li>{t("salesCustomerEdit")}</li>
        <li>{t("salesCustomerLink")}</li>
        <li>
          <Trans ns="help" i18nKey="salesDrafts" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="salesListPrice" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="salesConfirming" components={{ strong: <strong /> }} />
          <p>{t("salesCommitment")}</p>
        </li>
        {/* #727 — the only rule on this screen that PREVENTS rather than
            reports, so it sits with Confirming rather than with the discount
            display above it. */}
        <li>
          <Trans ns="help" i18nKey="salesDiscountCeiling" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="salesVoiding" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="salesPayments" components={{ strong: <strong /> }} />
        </li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="products">{t("productsHeading")}</h3>{openLink("products")}</div>
      <ul>
        <li>{t("productsWhatYouSell")}</li>
        <li>
          <Trans ns="help" i18nKey="productsPackedUnits" components={{ strong: <strong /> }} />
        </li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="grades">{t("gradesHeading")}</h3>{openLink("grades")}</div>
      <ul>
        <li>
          <Trans ns="help" i18nKey="gradesBuckets" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="gradesDeactivating" components={{ strong: <strong />, em: <em /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="gradesLowStockFloor" components={{ strong: <strong /> }} />
        </li>
        <li>{t("gradesAdminOnly")}</li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="reports">{t("reportsHeading")}</h3>{openLink("reports")}</div>
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

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="expenses">{t("expensesHeading")}</h3>{openLink("expenses")}</div>
      <ul>
        <li>{t("expensesRecording")}</li>
        <li>
          <Trans ns="help" i18nKey="expensesClearingFilters" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="expensesCorrections" components={{ strong: <strong /> }} />
        </li>
        <li>{t("expensesAdminOnly")}</li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="inventory">{t("inventoryHeading")}</h3>{openLink("inventory")}</div>
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

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="feed">{t("feedHeading")}</h3>{openLink("feed")}</div>
      <ul>
        <li>
          <Trans ns="help" i18nKey="feedRecording" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="feedCorrecting" components={{ strong: <strong /> }} />
          <p>{t("feedRationPreview")}</p>
        </li>
        <li>
          <Trans ns="help" i18nKey="feedDailyEntry" components={{ strong: <strong /> }} />
        </li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="water">{t("waterHeading")}</h3>{openLink("water")}</div>
      <ul>
        <li>
          <Trans ns="help" i18nKey="waterRecording" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="waterCorrecting" components={{ strong: <strong /> }} />
        </li>
        <li>{t("waterModes")}</li>
        <li>{t("waterLifecycle")}</li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
      <div className="help-section-head"><h3 id="roles">{t("rolesHeading")}</h3>{openLink("roles")}</div>
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

      </section>
    </>
  );
}
