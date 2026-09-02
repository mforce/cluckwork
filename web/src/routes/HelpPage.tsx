import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { Trans, useTranslation } from "react-i18next";
import { useInRouterContext, useLocation } from "react-router";
import { GLOSSARY, GLOSSARY_GROUPS } from "./helpGlossary";

// F18 (#71): in-app user guide + glossary. #52 restyled it into a docs layout
// with a sticky contents rail that scroll-spies the section in view; #657
// grouped the rail, moved "Fixing mistakes" up beside the daily loop, turned
// the glossary into grouped, alphabetised, deep-linkable definition lists
// (data in ./helpGlossary.ts), and put one search box over all of it. KEEP
// THIS PAGE CURRENT: the docs-sync rule (AGENTS.md) requires every
// user-visible change to update the relevant section here and
// specs/product/GLOSSARY.md in the same PR.

// The contents rail, grouped. Flattened it must mirror the <h3 id=...>
// sections below, in document order — a section missing here is invisible to
// anyone who navigates by the contents list, and HelpPage.test.tsx compares
// the two lists.
//
// Task 32 (B6a, #182): the label (2nd element) is a `help` catalog key, not
// literal text — rendered via t(label) at render time. The `id` (1st element)
// stays byte-identical: it drives the <h3 id=...> anchors below AND the
// scroll-spy IntersectionObserver in the effect further down. Do NOT rename
// an id — that would break both.
const RAIL = [
  { labelKey: "railGroupStartHere", entries: [
    ["getting-around", "tocGettingAround"],
    ["signing-in", "tocSigningIn"],
    ["daily-loop", "tocDailyLoop"],
    ["mistakes", "tocMistakes"],
    ["dialogs", "tocDialogs"],
  ] },
  { labelKey: "railGroupEveryDay", entries: [
    ["daily-entry", "tocDailyEntry"],
    ["flocks", "tocFlocks"],
    ["stock", "tocStock"],
    ["history", "tocHistory"],
  ] },
  { labelKey: "railGroupSelling", entries: [
    ["sales", "tocSales"],
    ["products", "tocProducts"],
    ["grades", "tocGrades"],
    ["reports", "tocReports"],
    ["expenses", "tocExpenses"],
  ] },
  { labelKey: "railGroupSupplies", entries: [
    ["inventory", "tocInventory"],
    ["feed", "tocFeed"],
    ["water", "tocWater"],
  ] },
  { labelKey: "railGroupFarm", entries: [
    ["roles", "tocRoles"],
    ["farm-settings", "tocFarmSettings"],
    ["farm-palette", "tocFarmPalette"],
    ["account", "tocAccount"],
    ["audit", "tocAudit"],
    ["export", "tocExport"],
  ] },
  { labelKey: "railGroupApp", entries: [
    ["install", "tocInstall"],
    ["glossary", "tocGlossary"],
  ] },
] as const;

type RailEntry = (typeof RAIL)[number]["entries"][number];
const TOC: readonly RailEntry[] = RAIL.flatMap((g) => g.entries as readonly RailEntry[]);

// Rendered only inside a router (the tests mount HelpPage bare): follows the
// router's own location, which is what a <Link to="/help#…"> changes.
function RouterHashScroll({ onHash }: { onHash: (hash: string) => void }) {
  const { hash } = useLocation();
  useEffect(() => { onHash(hash); }, [hash, onHash]);
  return null;
}

export function HelpPage() {
  const { t, i18n } = useTranslation("help");
  // The busy-button line (#236) reads the same `common` key as the
  // "Working…" announcement it explains, so the two can never drift.
  const { t: tc } = useTranslation("common");
  const searchId = useId();

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

  // #657 — a deep link (`/help#glossary-egg-lot`, from a GlossaryLink or a
  // pasted URL). The browser only scrolls to a fragment on a full load; a
  // client-side navigation lands at the top, so the target is scrolled to
  // here — on mount, and again whenever the fragment changes while the page
  // stays mounted (a GlossaryLink clicked from the Help page itself, or the
  // address bar edited). A router navigation is a pushState, which fires no
  // hashchange, so inside a router the location itself is watched too.
  //
  // A search in progress may be hiding the very entry the link names, so the
  // link clears the search first and scrolls once the target is back on
  // screen — that is what `pendingHash` waits for.
  const [query, setQuery] = useState("");
  const [pendingHash, setPendingHash] = useState<string | null>(null);
  const followHash = useCallback((hash: string) => {
    const id = decodeURIComponent(hash.slice(1));
    if (id === "") return;
    setQuery("");
    setPendingHash(id);
  }, []);
  useEffect(() => {
    const follow = () => followHash(window.location.hash);
    follow();
    window.addEventListener("hashchange", follow);
    return () => window.removeEventListener("hashchange", follow);
  }, [followHash]);
  useEffect(() => {
    if (pendingHash === null || query !== "") return;
    document.getElementById(pendingHash)?.scrollIntoView();
    setPendingHash(null);
  }, [pendingHash, query]);
  const inRouter = useInRouterContext();

  // #657 — one search over the guide sections and the glossary terms. The
  // sections are prose the catalog assembles at render time, so the match is
  // taken from the DOM text rather than from any list this page could keep:
  // every `[data-searchable]` element is hidden when its text does not carry
  // the query, and a glossary group folds when none of its terms survive.
  const [matches, setMatches] = useState<{ sections: number; terms: number } | null>(null);
  const bodyRef = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    const body = bodyRef.current;
    if (body === null) return;
    const q = query.trim().toLowerCase();
    let sections = 0;
    let terms = 0;
    for (const node of Array.from(body.querySelectorAll<HTMLElement>("[data-searchable]"))) {
      const hit = q === "" || (node.textContent ?? "").toLowerCase().includes(q);
      node.hidden = !hit;
      if (!hit) continue;
      if (node.dataset.searchable === "term") terms += 1;
      else if (node.dataset.searchable === "section") sections += 1;
    }
    for (const group of Array.from(body.querySelectorAll<HTMLElement>(".glossary-group"))) {
      group.hidden = q !== "" && group.querySelector(".glossary-entry:not([hidden])") === null;
    }
    setMatches(q === "" ? null : { sections, terms });
    // i18n.language: a language switch re-renders every section's text, so an
    // active query is re-applied to the new words.
  }, [query, i18n.language]);

  const searchStatus = matches === null
    ? ""
    : matches.sections + matches.terms === 0
      ? t("searchNoMatches", { query: query.trim() })
      : t("searchMatches", { query: query.trim(), sections: matches.sections, terms: matches.terms });

  return (
    <section className="help">
      {inRouter && <RouterHashScroll onHash={followHash} />}
      <div className="help-head">
        <p className="help-kicker">{t("eyebrow")}</p>
        <h2>{t("heading")}</h2>
        <p className="help-lead">{t("lead")}</p>
      </div>

      <div className="help-search">
        <label htmlFor={searchId}>{t("searchLabel")}
          <input id={searchId} type="search" value={query} placeholder={t("searchPlaceholder")}
            autoComplete="off" onChange={(e) => setQuery(e.target.value)} />
        </label>
        {query !== "" && (
          <button type="button" className="link" onClick={() => setQuery("")}>{t("searchClear")}</button>
        )}
      </div>
      <p className="help-search-status" role="status" aria-live="polite">{searchStatus}</p>

      <div className="help-layout">
        <nav className="help-toc" aria-label={t("contentsAriaLabel")}>
          <p className="help-kicker">{t("contentsEyebrow")}</p>
          {RAIL.map((group) => (
            <div key={group.labelKey}>
              <p className="help-toc-group">{t(group.labelKey)}</p>
              <ul>
                {group.entries.map(([id, labelKey]) => (
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
            </div>
          ))}
        </nav>

        <div className="help-body" ref={bodyRef}>
      <section className="help-section" data-searchable="section">
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
        <li>
          {/* #512 — the shared searchable pickers (flock/customer name fields). */}
          <Trans ns="help" i18nKey="gettingAroundSearchablePicker" components={{ strong: <strong /> }} />
        </li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
      <h3 id="daily-loop">{t("dailyLoopHeading")}</h3>
      <p>
        <Trans ns="help" i18nKey="dailyLoopChain" components={{ strong: <strong /> }} />
      </p>
      <p className="muted">{t("dailyLoopSummary")}</p>

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
      <h3 id="sales">{t("salesHeading")}</h3>
      <ul>
        <li>{t("salesCustomerEdit")}</li>
        <li>{t("salesCustomerLink")}</li>
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

      </section>

      <section className="help-section" data-searchable="section">
      <h3 id="products">{t("productsHeading")}</h3>
      <ul>
        <li>{t("productsWhatYouSell")}</li>
        <li>
          <Trans ns="help" i18nKey="productsPackedUnits" components={{ strong: <strong /> }} />
        </li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
      <h3 id="expenses">{t("expensesHeading")}</h3>
      <ul>
        <li>{t("expensesRecording")}</li>
        <li>
          <Trans ns="help" i18nKey="expensesCorrections" components={{ strong: <strong /> }} />
        </li>
        <li>{t("expensesAdminOnly")}</li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
      <h3 id="farm-palette">{t("farmPaletteHeading")}</h3>
      <p>{t("farmPaletteIntro")}</p>
      <p>{t("farmPaletteLightNight")}</p>

      {/* #444 — the personal counterpart to Farm settings: what each signed-in
          person can set for themselves, whatever their role. */}
      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="section">
      <h3 id="export">{t("exportHeading")}</h3>
      <ul>
        <li>
          <Trans ns="help" i18nKey="exportCsv" components={{ strong: <strong /> }} />
        </li>
        <li>{t("exportFormats")}</li>
      </ul>

      </section>

      <section className="help-section" data-searchable="section">
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

      </section>

      <section className="help-section" data-searchable="glossary">
      <h3 id="glossary">{t("glossaryHeading")}</h3>
      {GLOSSARY_GROUPS.map((group) => {
        // Alphabetical in the ACTIVE language — a Spanish reader gets Spanish
        // order, not English order wearing Spanish labels.
        const entries = GLOSSARY
          .filter((e) => e.group === group.key)
          .map((e) => ({ ...e, term: t(e.termKey) }))
          .sort((a, b) => a.term.localeCompare(b.term, i18n.language));
        return (
          <div key={group.key} className="glossary-group">
            <h4 id={`glossary-group-${group.key}`}>{t(group.labelKey)}</h4>
            <dl className="glossary">
              {entries.map((e) => (
                <div key={e.key} id={e.id} className="glossary-entry" data-searchable="term">
                  <dt><a href={`#${e.id}`}>{e.term}</a></dt>
                  <dd>
                    {e.rich
                      ? <Trans ns="help" i18nKey={e.defKey} components={{ strong: <strong /> }} />
                      : t(e.defKey)}
                  </dd>
                </div>
              ))}
            </dl>
          </div>
        );
      })}

          <p className="muted">
            <Trans ns="help" i18nKey="glossaryRepoNote" components={{ code: <code /> }} />
          </p>
      </section>
        </div>
      </div>
    </section>
  );
}
