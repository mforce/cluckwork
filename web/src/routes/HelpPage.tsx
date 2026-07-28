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
  ["water", "tocWater"],
  ["sales", "tocSales"],
  ["reports", "tocReports"],
  ["expenses", "tocExpenses"],
  ["history", "tocHistory"],
  ["audit", "tocAudit"],
  ["export", "tocExport"],
  ["farm-settings", "tocFarmSettings"],
  ["farm-palette", "tocFarmPalette"],
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
        <li>
          <Trans ns="help" i18nKey="gettingAroundErrorScreen" components={{ strong: <strong /> }} />
        </li>
      </ul>

      <h3 id="signing-in">{t("signingInHeading")}</h3>
      <ul>
        <li>
          <Trans ns="help" i18nKey="signingInBasic" components={{ strong: <strong /> }} />
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
          {/* #236 — the pending-save indicator, in the section that already
              explains save/retry behaviour. */}
          {tc("workingHint")}
        </li>
        <li>
          <Trans ns="help" i18nKey="dialogsInlineForms" components={{ strong: <strong /> }} />
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
          <Trans ns="help" i18nKey="reportsMoney" components={{ strong: <strong /> }} />
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
        <li>
          <Trans ns="help" i18nKey="farmSettingsLogo" components={{ strong: <strong /> }} />
        </li>
        <li>
          <Trans ns="help" i18nKey="farmSettingsSquareLogo" components={{ strong: <strong /> }} />
        </li>
      </ul>

      <h3 id="farm-palette">{t("farmPaletteHeading")}</h3>
      <p>{t("farmPaletteIntro")}</p>
      <p>{t("farmPaletteLightNight")}</p>

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

      <h3 id="glossary">Glossary</h3>
      <table className="data">
        <tbody>
          <tr><th scope="row">Navigation</th>
            <td>Screens live in the left sidebar on a computer; on a phone the four you use most are tabs across the bottom, the rest under More.</td></tr>
          <tr><th scope="row">Operational day</th>
            <td>Dates mean your farm&apos;s calendar day, worked out from the farm&apos;s own timezone rather than a clock somewhere else. It is the same &quot;today&quot; everywhere: what counts as a future date when you record work, when eggs leave a withdrawal period, which eggs a sale can take, the day a flock is depleted or archived on, and the range reports open on. Every field that records WHEN SOMETHING HAPPENED opens on it and will not go past it, whatever day the device in your hand is on. Dates meant to fall in the future are not capped — a feed batch&apos;s expiry, and the History and Water filters.</td></tr>
          <tr><th scope="row">Install to home screen</th>
            <td>Adding Cluckwork to a phone or tablet&apos;s home screen from the browser, so it gets its own icon and opens in its own window without the browser bars. It is the same app, not a separate download — nothing to update from an app store. Only offered over a secure (https) address, and it does <strong>not</strong> make the app work offline: it still needs a connection to load and save.</td></tr>
          <tr><th scope="row">A new version is ready</th>
            <td>After a release, an installed app notices the new version in the background and asks before switching, rather than reloading while you are typing. Press Reload when convenient, or Later and it asks again next time. Nothing is lost by leaving it — the running app keeps working until you accept.</td></tr>
          <tr><th scope="row">Too many sign-in attempts</th>
            <td>Sign-in is rate limited to slow password guessing: too many attempts from one place in a few minutes are refused with this message until a short cool-off passes. It never affects an already signed-in session.</td></tr>
          <tr><th scope="row">&quot;Something went wrong&quot; screen</th>
            <td>What a screen shows when it hits an error, instead of going blank. Saved data is safe — anything you were still typing may need re-entering; tap Reload or Back to the dashboard. &quot;Error details&quot; holds the message for a screenshot.</td></tr>
          <tr><th scope="row">Daily entry</th>
            <td>One flock's day: eggs by grade, losses, deaths. Draft until submitted.</td></tr>
          <tr><th scope="row">Egg lot</th>
            <td>A dated batch of sellable eggs of one grade, created by submitting an entry. Stock is the sum of lots.</td></tr>
          <tr><th scope="row">Grade</th>
            <td>A grading bucket (size, quality, or custom). Saleable grades can be sold.</td></tr>
          <tr><th scope="row">Egg movement ledger</th>
            <td>The line-by-line history behind an egg lot&apos;s balance: production in, sales out, corrections and voids signed accordingly.</td></tr>
          <tr><th scope="row">FIFO</th>
            <td>"First in, first out" — sales and feed usage always take the oldest stock first.</td></tr>
          <tr><th scope="row">Cull</th>
            <td>Birds deliberately removed from a flock (sold, slaughtered, given away) — not deaths.</td></tr>
          <tr><th scope="row">Mortality</th>
            <td>Deaths, recorded on the daily entry; lands in the bird ledger automatically at submit.</td></tr>
          <tr><th scope="row">Deplete</th>
            <td>Mark a flock as having no birds left. History stays; reversible via Reactivate.</td></tr>
          <tr><th scope="row">Archive</th>
            <td>Hide a finished flock from daily work. Reversible via Reactivate.</td></tr>
          <tr><th scope="row">Withdrawal restriction</th>
            <td>A hold on eggs during a medication withholding period. Coming with medication tracking — nothing sets restrictions yet, so manage withholding periods outside Cluckwork for now.</td></tr>
          <tr><th scope="row">Product</th>
            <td>What you sell — an egg product points at a grade (its stock source) and carries a selling unit and default price.</td></tr>
          <tr><th scope="row">Packed unit</th>
            <td>How many eggs a dozen/tray/carton/case holds on your farm. Each sale line keeps the count it was sold with.</td></tr>
          <tr><th scope="row">Sales line</th>
            <td>One product on an order: quantity in selling units, priced per unit; the eggs behind it are quantity × the unit&apos;s egg count.</td></tr>
          <tr><th scope="row">Confirm (order)</th>
            <td>Turns a draft order into a real sale and allocates stock. Undone only by voiding.</td></tr>
          <tr><th scope="row">Void (order)</th>
            <td>Undo of a mistaken confirm — stock returns to the exact lots it came from. Needs a reason.</td></tr>
          <tr><th scope="row">Cancel (order)</th>
            <td>Close a draft that never happened. No stock involved.</td></tr>
          <tr><th scope="row">Inventory item</th>
            <td>A catalog entry for something you stock (feed, supplements…), with a fixed unit of measure.</td></tr>
          <tr><th scope="row">Inventory lot</th>
            <td>One received batch of an item, with its own cost. On-hand = sum of lots.</td></tr>
          <tr><th scope="row">Inventory movement ledger</th>
            <td>The append-only trail of every feed/supply stock change. Corrections are new rows, never edits.</td></tr>
          <tr><th scope="row">Water usage</th>
            <td>What a flock drank on a day — direct amount or meter delta. Editable in place; flock/date fixed.</td></tr>
          <tr><th scope="row">Feed usage</th>
            <td>What a flock ate on a day; drains lots FIFO and estimates cost from them.</td></tr>
          <tr><th scope="row">Adjustment / Discard</th>
            <td>Stock corrections against a lot, reason required. Discard = write-off (spoilage).</td></tr>
          <tr><th scope="row">Roles</th>
            <td>Admin (owner), Manager, Worker, Sales, Read-only — see "Who can do what". Workers record; managers also correct and configure; sales handles orders and payments; read-only just views.</td></tr>
          <tr><th scope="row">Locked (entry)</th>
            <td>A submitted entry older than 7 days — closed to routine edits; admin adjust/void still works.</td></tr>
          <tr><th scope="row">Adjust (entry)</th>
            <td>Admin correction of a submitted entry. Stock and bird ledger reconcile automatically; sold eggs are untouchable; previous values stay visible.</td></tr>
          <tr><th scope="row">Void (entry)</th>
            <td>Admin undo of a whole submitted entry — lots empty, deaths reverse, entry preserved as Voided. Refused once its eggs are sold.</td></tr>
          <tr><th scope="row">Farm settings</th>
            <td>The farm&apos;s name, timezone, locale, currency and unit system, plus optional first day of week and date/time formats. Setup → Farm settings; owners and managers edit, everyone reads — formatting money and dates is not a permission.</td></tr>
          <tr><th scope="row">Currency lock</th>
            <td>The farm currency stops being editable once anything has recorded an amount in it — a sale, a payment, an expense, a priced product, money spent on feed. The field shows locked with the reason. Nothing already recorded is ever re-priced, which is the whole point.</td></tr>
          <tr><th scope="row">Farm logo</th>
            <td>Your own image in place of the Cluckwork mark in the sidebar, uploaded from Farm settings. PNG, JPEG or WebP (2 MB by default), still images only; a square, simple mark reads best at the small sidebar size. Stored as a rebuilt copy with camera and location details stripped out.</td></tr>
          <tr>
            <th scope="row">Farm palette</th>
            <td>
              The farm-wide accent colour, chosen by an admin in Farm settings.
              Separate from each person's own light/night mode setting.
            </td>
          </tr>
          <tr><th scope="row">UI language</th>
            <td>The per-user language the interface is shown in — English, Español, or Tagalog — chosen from Account → Preferences. English is the fallback for any screen not yet translated, whatever language you picked.</td></tr>
        </tbody>
      </table>

          <p className="muted">
            Full spec-language definitions live in the repository's{" "}
            <code>specs/product/GLOSSARY.md</code>.
          </p>
        </div>
      </div>
    </section>
  );
}
