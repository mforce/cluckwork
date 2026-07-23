import { useEffect, useState } from "react";

// F18 (#71): in-app user guide + glossary. #52 restyled it into a docs layout
// with a sticky contents rail that scroll-spies the section in view. KEEP THIS
// PAGE CURRENT: the docs-sync rule (AGENTS.md) requires every user-visible
// change to update the relevant section here and specs/product/GLOSSARY.md in
// the same PR.

// Must mirror the <h3 id=...> sections below, in document order — a section
// missing here is invisible to anyone who navigates by the contents list.
const TOC = [
  ["getting-around", "Getting around"],
  ["signing-in", "Signing in"],
  ["daily-loop", "The daily loop"],
  ["roles", "Who can do what"],
  ["dialogs", "Adding & correcting"],
  ["daily-entry", "Daily entry"],
  ["flocks", "Flocks & birds"],
  ["grades", "Egg grades"],
  ["products", "Products"],
  ["stock", "Stock"],
  ["inventory", "Feed & inventory"],
  ["water", "Water"],
  ["sales", "Customers & sales"],
  ["reports", "Reports"],
  ["expenses", "Expenses"],
  ["history", "History"],
  ["audit", "Audit log"],
  ["export", "Export & backup"],
  ["farm-settings", "Farm settings"],
  ["mistakes", "Fixing mistakes"],
  ["glossary", "Glossary"],
] as const;

export function HelpPage() {
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
        <p className="eyebrow">User guide</p>
        <h2>Help</h2>
        <p className="help-lead">
          How Cluckwork works, screen by screen — and how to undo mistakes.
        </p>
      </div>

      <div className="help-layout">
        <nav className="help-toc" aria-label="Help contents">
          <p className="eyebrow">Contents</p>
          <ul>
            {TOC.map(([id, label]) => (
              <li key={id}>
                <a
                  href={`#${id}`}
                  className={activeId === id ? "active" : undefined}
                  aria-current={activeId === id ? "location" : undefined}
                  onClick={() => setActiveId(id)}
                >
                  {label}
                </a>
              </li>
            ))}
          </ul>
        </nav>

        <div className="help-body">
          <h3 id="getting-around">Getting around</h3>
      <ul>
        <li>
          On a computer, every screen sits in the <strong>sidebar</strong> on
          the left, grouped by job.
        </li>
        <li>
          On a phone, the screens you use most are <strong>tabs across the
          bottom</strong>, in easy thumb reach. Which four you get depends on
          your role — a worker gets Daily entry, someone in sales gets Sales.
          Everything else is one tap away under <strong>More</strong>.
        </li>
        <li>
          If a screen ever shows <strong>"Something went wrong"</strong>, that is
          the app catching an error instead of leaving you on a blank page.
          Anything you had already saved is safe (anything you were still typing
          may need to be entered again) — tap <strong>Reload</strong>, or{" "}
          <strong>Back to the dashboard</strong> and try again. If it keeps
          happening, open "Error details" and send a screenshot.
        </li>
      </ul>

      <h3 id="signing-in">Signing in</h3>
      <ul>
        <li>
          Sign in with the email and password your administrator set up. A wrong
          password just says <strong>Invalid email or password</strong> — try
          again.
        </li>
        <li>
          To slow down anyone guessing passwords, sign-in attempts from the same
          place are <strong>limited</strong>. After too many tries in a few
          minutes you&apos;ll see <strong>&quot;Too many sign-in
          attempts&quot;</strong> — that isn&apos;t a fault, just wait a few
          minutes and try again. Being <em>signed in</em> already is never
          affected; your work carries on normally.
        </li>
        <li>
          Separately, too many wrong passwords for <em>one account</em> briefly
          lock <em>that</em> account. While it is locked, even the correct
          password still says <strong>Invalid email or password</strong>. The
          lock is temporary — wait up to about 15 minutes and try again.
        </li>
        <li>
          Your sign-in is kept in your browser securely and stays active as you
          work, even across reloads. After the app is <strong>updated</strong> you
          may be asked to sign in once more — that&apos;s expected.
        </li>
      </ul>

      <h3 id="daily-loop">The daily loop</h3>
      <p>
        Everything in Cluckwork hangs off one chain: you record a <strong>daily
        entry</strong> for each flock (eggs by grade, losses, deaths), you{" "}
        <strong>submit</strong> it, and submitting creates dated <strong>egg
        lots</strong> — that's your sellable <strong>stock</strong>. A{" "}
        <strong>sales order</strong> takes from stock when you confirm it,
        always oldest eggs first. Feed flows the same way on the input side:
        purchases put feed into stock, daily usage draws it down per flock.
      </p>
      <p className="muted">
        Record entry → submit → egg lots → stock → order → confirm.
      </p>

      <h3 id="roles">Who can do what</h3>
      <ul>
        <li>
          Five kinds of sign-in. <strong>Workers</strong> run the daily loop —
          record and submit entries, receive feed, record feed and water usage,
          create flocks and customers, take orders from draft through confirm.
          A worker can be narrowed to <strong>assigned flocks</strong>: with no
          assignments they can record for any flock; the first assignment
          restricts them to the listed ones.
        </li>
        <li>
          <strong>Managers</strong> do everything workers do, plus everything
          that <strong>undoes, corrects, or configures</strong>: voids, stock
          and water corrections, flock lifecycle, culls, the grade/product/item
          catalogs, expenses, money reports, the audit log, and exports.
        </li>
        <li>
          <strong>Sales</strong> sign-ins handle customers, orders, and{" "}
          <strong>payments</strong> — but no production capture and no
          expenses. <strong>Read-only</strong> sign-ins see stock, history, and
          reports, and can change nothing.
        </li>
        <li>
          <strong>Admin (owner)</strong> does everything a manager does and is
          the only role that manages users: creating sign-ins on the{" "}
          <strong>Users</strong> screen (email, password, role) and assigning
          workers to flocks. Changing an existing user&apos;s role or password
          comes with a later release. Controls you can&apos;t use are hidden,
          and the server refuses them regardless.
        </li>
      </ul>

      <h3 id="dialogs">Adding &amp; correcting</h3>
      <ul>
        <li>
          Adding and correcting happen in a popup. Look for the{" "}
          <strong>New …</strong> button beside the screen&apos;s title — new
          grade, product, customer, flock, item, user, order. Each row&apos;s{" "}
          <strong>edit</strong> or <strong>correct</strong> link opens the same
          popup with that row&apos;s values already filled in.
        </li>
        <li>
          Drill-downs work the same way. Open a flock&apos;s{" "}
          <strong>birds</strong> ledger to record a cull, an inventory item to
          record a purchase, feed usage, or a stock correction, an order to{" "}
          <strong>record a payment</strong>, or a worker&apos;s{" "}
          <strong>flocks</strong> to manage their access — the ledger stays
          where it is and the form comes to you.
        </li>
        <li>
          <strong>Cancel</strong>, Escape, or a click outside closes the popup
          and changes nothing. If a save fails, the popup stays open with your
          values and the reason, so you can fix it and try again — retrying is
          safe, it never records the same thing twice.
        </li>
        <li>
          The screens whose whole job is capture keep their form on the page:{" "}
          <strong>Daily entry</strong>, <strong>Water</strong>, recording an
          expense, and adding lines to a draft order. Those you use every day —
          no extra click.
        </li>
        <li>
          <strong>Actions that cannot be undone ask first.</strong> Submitting
          a day, confirming or cancelling an order, depleting or archiving a
          flock — each one says what is about to happen and waits. The keyboard
          starts on <strong>Cancel</strong>, so pressing Enter by habit never
          goes through with it. A <strong>red</strong> button means the action
          undoes or retires something: voiding, cancelling a draft, depleting,
          archiving. Submitting a day and confirming an order cannot be taken
          back either, but they are the normal way through the week.
        </li>
        <li>
          <strong>Voids need a reason.</strong> Voiding a daily entry, a
          payment, or a confirmed order asks the same way but wants a written
          reason first — it is stored with the void and shown wherever that
          record turns up afterwards, so write what actually happened. Leave it
          empty and the popup says so on the spot, keeping whatever you typed.
        </li>
      </ul>

      <h3 id="daily-entry">Daily entry</h3>
      <ul>
        <li>
          Pick the flock and date at the top, then work through two panes side
          by side: <strong>1 Egg counts</strong> (total, cracked, dirty,
          discarded, deaths) and <strong>2 Grading</strong>. The counts produce
          a <strong>sellable</strong> figure, and that is the number the grades
          have to add up to — they can never exceed it.
        </li>
        <li>
          Grading counts <strong>down</strong>. Beside the grades is how many
          sellable eggs you still have to place; it turns green the moment the
          day adds up and red if you go over. You cannot submit while it is over.
        </li>
        <li>
          Every count has <strong>−</strong> and <strong>+</strong> buttons.
          Tap for one, or <strong>hold</strong> — it speeds up as you go, so a
          few hundred eggs takes about a second. Easier than a keypad with
          gloves on. A grade&apos;s <strong>+</strong> stops once the day is
          fully graded, so you cannot overshoot with it.
        </li>
        <li>
          Most days end the same way — one grade takes whatever is left.{" "}
          <strong>Put all in…</strong> beside the remaining count does it in one
          move: drag it onto a grade, or tap it and pick one.
        </li>
        <li>
          Both save buttons stay in a bar at the bottom of the screen as you
          scroll. On a phone that bar also shows the sellable count and how many
          are left, so you never lose sight of whether the day adds up.
        </li>
        <li>
          <strong>Save draft</strong> keeps the day editable. <strong>Submit</strong>{" "}
          makes it official: it creates the day's egg lots and records deaths in
          the flock's bird ledger. Workers can no longer edit it — an admin can
          adjust or void it (see "Fixing mistakes").
        </li>
        <li>
          Submitted entries <strong>lock automatically after 7 days</strong>.
          Locked only means the correction window for routine fixes has
          passed — admin adjust/void still works on locked entries.
        </li>
        <li>
          &quot;Today&quot; means <strong>your farm&apos;s today</strong>, not the
          clock in some other part of the world. You can record any day up to and
          including it; a day that has not happened yet on the farm is refused —
          here and everywhere else you enter a date: feed and water use, feed
          purchases and stock corrections, expenses, payments, and a flock&apos;s
          placement date. The same date decides when eggs come out of a
          withdrawal period, which eggs a sale can take, the day a flock is
          depleted or archived on, and the range reports open on — so nothing
          disagrees about what day it is.
        </li>
        <li>
          One entry per flock per day. Reopening a day that has a draft loads it
          for editing and shows an <strong>Editing draft</strong> badge beside
          the title, so picking up saved work never looks like starting fresh.
          If prefill fails, saving is blocked until it succeeds so an existing
          draft is never silently overwritten.
        </li>
        <li>
          Depleted flocks accept backfilled entries up to their depletion date;
          archived flocks accept none.
        </li>
      </ul>

      <h3 id="flocks">Flocks &amp; birds</h3>
      <ul>
        <li>
          A flock's <strong>current birds</strong> = its starting count minus
          everything in its <strong>bird ledger</strong>: deaths (added
          automatically when entries are submitted), <strong>culls</strong>{" "}
          (birds deliberately removed — sold, slaughtered, given away), and
          manual <strong>adjustments</strong> (count corrections, either
          direction).
        </li>
        <li>
          Lifecycle: <strong>Active</strong> (normal) → <strong>Depleted</strong>{" "}
          (birds gone; history stays, backfill allowed) → <strong>Archived</strong>{" "}
          (hidden from daily work). Depleting and archiving ask for
          confirmation; both are reversible with <strong>Reactivate</strong>.
        </li>
        <li>
          Anyone can create a flock and view the bird ledger. Editing a flock,
          lifecycle changes, and recording culls/adjustments are admin-only.
        </li>
      </ul>

      <h3 id="grades">Egg grades</h3>
      <ul>
        <li>
          Grades are your farm's grading buckets — sizes (Large…), qualities
          (Cracked…), or custom. Only <strong>saleable</strong> grades appear in
          entry capture and on orders; non-saleable buckets are bookkeeping.
        </li>
        <li>
          Grades are never deleted. <strong>Deactivating</strong> removes a
          grade from capture and order pickers: its stock stays counted and
          order lines added earlier can still confirm, but it can't be put on{" "}
          <em>new</em> order lines — reactivate the grade to sell remaining
          stock. History keeps showing its name.
        </li>
        <li>
          The grade catalog is configuration — managing it is admin-only.
        </li>
      </ul>

      <h3 id="products">Products (admin)</h3>
      <ul>
        <li>
          Products are what you sell — &quot;Large Eggs by the dozen&quot;,
          &quot;Mixed carton&quot;. Each egg product points at an egg grade
          (that&apos;s where its stock comes from) and carries a selling unit
          and an optional default price. Only egg products exist for now.
        </li>
        <li>
          <strong>Packed units</strong> set how many eggs each unit holds —
          your carton might be 12, 18, or 30. Changing a unit only affects
          future sales; past orders keep the count they were sold with.
        </li>
      </ul>

      <h3 id="stock">Stock</h3>
      <ul>
        <li>
          Every grade expands into its <strong>lots</strong> (one per
          submitted day), and every lot into its <strong>movement
          ledger</strong> — an explicit line for each production, sale,
          correction, or void. The running sum always equals the balance
          shown; nothing changes stock without leaving a line.
        </li>
        <li>
          Stock is the sum of your egg lots per grade. The{" "}
          <strong>restricted</strong> column is reserved for medication
          withholding periods — that feature arrives with medication tracking.{" "}
          <strong>Nothing marks eggs restricted yet, so the system does not
          enforce withdrawal times today</strong> — manage withholding periods
          outside Cluckwork for now.
        </li>
        <li>
          Selling always takes the oldest lots first, so stock naturally rotates.
        </li>
      </ul>

      <h3 id="inventory">Feed &amp; inventory</h3>
      <ul>
        <li>
          <strong>Items</strong> define what you track (feed, supplements…) and
          the unit it's measured in. The unit locks once stock has been received
          — quantities on record must keep meaning what they meant.
        </li>
        <li>
          <strong>Record purchase</strong> books received stock as a dated lot
          with its cost. <strong>Record usage</strong> logs what a flock ate on
          a day: it draws from the oldest lots first (only lots that existed on
          that date) and estimates the cost from the actual lots consumed.
        </li>
        <li>
          Every change lands in the item's <strong>movement ledger</strong> —
          purchases, usage, corrections. Ledger rows are never edited or
          deleted.
        </li>
        <li>
          Typos and spoilage are fixed with <strong>corrections</strong>: an{" "}
          <em>Adjustment</em> (either direction) or a <em>Discard</em>{" "}
          (write-off) against a specific lot, always with a reason. The original
          row and the correction both stay visible.
        </li>
        <li>
          Recording purchases and usage is open to everyone; the item catalog
          and stock corrections are admin-only.
        </li>
      </ul>

      <h3 id="water">Water</h3>
      <ul>
        <li>
          Record what each flock drank per day: either a direct amount (liters
          or gallons) or <strong>meter readings</strong> — the amount is then
          the meter delta (end − start).
        </li>
        <li>
          Water records have no stock behind them, so mistakes are fixed by{" "}
          <strong>correcting the record directly</strong> (the "correct" button,
          admin-only) — no compensating entries. The flock and date are fixed:
          picked wrong, record it again under the right one.
        </li>
        <li>
          Same lifecycle rule as everywhere: depleted flocks accept backfill up
          to their depletion date, archived flocks accept nothing.
        </li>
      </ul>

      <h3 id="sales">Customers &amp; sales</h3>
      <ul>
        <li>
          Orders start as <strong>drafts</strong>: add lines by picking a{" "}
          <strong>product</strong>, a packed unit (dozen, carton, …), a
          quantity, and a price per unit (prefilled from the product&apos;s
          default) — edit freely, or <strong>cancel</strong> (the draft is
          kept, read-only). Each line remembers how many eggs its unit held
          when it was added, so redefining a carton later never changes old
          orders.
        </li>
        <li>
          <strong>Confirming</strong> an order allocates real stock — oldest
          lots first — and is the point where inventory changes hands.
        </li>
        <li>
          A mistaken confirm is undone with <strong>Void</strong> (admin-only,
          reason required): the eggs go back to the exact lots they came from,
          and the order stays listed as Voided. Voiding is for mistakes, not for
          returns of delivered goods. (Orders confirmed before lot-level
          allocation tracking existed can't self-serve void — ask your
          administrator.)
        </li>
        <li>
          <strong>Payments</strong> (Sales, Manager, or admin — voiding a
          payment is admin/manager only): a confirmed order's panel shows
          its settlement history — record partial payments (date, amount,
          method, optional reference) until the outstanding amount reaches
          zero; overpaying is refused. A wrong payment is <strong>voided</strong>{" "}
          (reason required) and the outstanding grows back. An order with
          payments can't be voided until its payments are voided first. The
          Customers page shows each customer's outstanding balance.
        </li>
      </ul>

      <h3 id="reports">Reports</h3>
      <ul>
        <li>
          <strong>Production</strong> (everyone): pick a date range — per-day
          eggs, losses, sellable, deaths, and <strong>hen-day %</strong> (eggs
          collected ÷ birds alive that day × 100), with period totals and a
          by-grade breakdown. Draft and voided entries don't count.
        </li>
        <li>
          <strong>Money</strong> (admin): sales summary for the range's orders
          (revenue / paid / outstanding), expenses by category, and{" "}
          <strong>basic profit</strong> — confirmed revenue minus recorded
          expenses, no cost-of-goods.
        </li>
      </ul>

      <h3 id="expenses">Expenses (admin)</h3>
      <ul>
        <li>
          Record money going out: date, category, description, and amount (in
          the farm's currency), optionally tied to a flock. The month picker
          shows a running total; categories are managed on the same screen
          (deactivating one hides it from new expenses — recorded ones keep
          it).
        </li>
        <li>
          Corrections edit the expense in place (<strong>correct</strong> on
          the row). If someone else corrected it first, the form reloads their
          values and asks you to re-apply. The currency an expense was
          recorded in never changes.
        </li>
        <li>
          Expenses are money data, so the whole screen — viewing included — is
          admin-only, unlike the production screens where workers record.
        </li>
      </ul>

      <h3 id="history">History</h3>
      <ul>
        <li>
          Browse recorded daily entries newest-first, filtered by flock and date
          range. The status column shows the entry's life: Draft, Submitted,
          Locked (7+ days old), Adjusted (hover for the reason), or Voided.
        </li>
        <li>
          Admins correct from here: <strong>adjust</strong> opens the entry's
          numbers for editing (reason required), <strong>void</strong> undoes
          the whole entry. Stock and the bird ledger follow automatically.
        </li>
        <li>
          Draft rows have an <strong>edit</strong> link (everyone, not just
          admins) that jumps back to the Daily entry screen with that flock
          and day loaded — drafts are edited there, not adjusted.
        </li>
      </ul>

      <h3 id="audit">Audit log (admin)</h3>
      <ul>
        <li>
          Every corrective, destructive, or configuration change lands in the
          audit log automatically: who did it, when (UTC), what it touched,
          and the reason where one was given. Written together with the change
          itself — a failed action leaves no trace, a successful one always
          does — and never editable, by anyone.
        </li>
      </ul>

      <h3 id="export">Export &amp; backup (admin)</h3>
      <ul>
        <li>
          The Export screen downloads your data as CSV files you can open in
          any spreadsheet — one dataset at a time, or everything at once as a
          zip (the <strong>full backup</strong>, with a manifest of row
          counts). Keep a copy somewhere safe on your own schedule; automatic
          scheduled backups come in a later phase.
        </li>
        <li>
          Money columns hold minor units (cents) plus the currency — exact
          values, not display formatting. Dates are ISO (YYYY-MM-DD), and
          timestamps are UTC.
        </li>
      </ul>

      <h3 id="farm-settings">Farm settings (admin)</h3>
      <ul>
        <li>
          <strong>Setup → Farm settings</strong> holds the farm&apos;s name and
          the four things that decide how everything reads:{" "}
          <strong>timezone</strong>, <strong>locale</strong>,{" "}
          <strong>currency</strong> and <strong>unit system</strong>. First day
          of week and the date and time formats are optional — leave them blank
          and the locale decides. The timezone takes effect everywhere the
          moment it is saved; the rest are recorded against the farm and will
          drive how amounts, dates and measurements are displayed once that
          formatting lands.
        </li>
        <li>
          The <strong>timezone</strong> is the farm&apos;s day. Every field that
          records <em>when something happened</em> — daily entry, flocks, water,
          feed usage and purchases, expenses, orders and payments — opens on it
          and refuses to go past it, whatever day the phone or laptop in your
          hand is on, so a device travelling ahead of the farm can no longer
          offer a date the save then refuses. Dates that are meant to be in the
          future are not capped: a feed batch&apos;s <strong>expiry</strong>, and
          the date ranges you filter History and Water by.
        </li>
        <li>
          The <strong>currency</strong> locks the moment the farm records its
          first amount — a sale, a payment, an expense, a priced product, or
          money spent on feed. The field shows as locked with the reason rather
          than letting you type a code that would be refused. Nothing already
          recorded is ever re-priced, which is exactly why it locks.
        </li>
        <li>
          The <strong>logo</strong> replaces the Cluckwork mark in the sidebar
          for everyone at the farm. PNG, JPEG or WebP, up to the size limit shown
          on the screen (2 MB by default) and 4096 pixels a side.
          Animated images are refused rather than flattened. What gets stored
          is a rebuilt copy:
          camera and location details are stripped out on the way in — a photo
          taken on a phone carries where it was taken, and for a farm that is
          its address. Remove it and the sidebar goes back to the Cluckwork
          mark.
        </li>
      </ul>

      <h3 id="mistakes">Fixing mistakes</h3>
      <p className="muted">
        Every fix in this table needs an admin sign-in (see "Who can do what")
        — workers record, admins correct. The one exception: a <em>draft</em>{" "}
        is still recording, not correcting, so workers edit their own drafts.
      </p>
      <table className="data">
        <thead>
          <tr><th>Mistake</th><th>Fix</th></tr>
        </thead>
        <tbody>
          <tr>
            <td>Depleted or archived the wrong flock</td>
            <td>Flocks → <strong>Reactivate</strong> (fully reversible)</td>
          </tr>
          <tr>
            <td>Wrong bird count</td>
            <td>Flocks → bird ledger → <strong>Adjustment</strong> (either direction)</td>
          </tr>
          <tr>
            <td>Confirmed the wrong sales order</td>
            <td>
              Sales → open the order → <strong>Void order</strong> (stock
              returns to its lots; reason required). If payments were recorded
              on it, void those first.
            </td>
          </tr>
          <tr>
            <td>Recorded a wrong payment</td>
            <td>
              Sales → open the order → payments → <strong>void</strong> (reason
              required): the row is kept and the outstanding amount grows back.
            </td>
          </tr>
          <tr>
            <td>Wrong <em>quantity</em> in a feed purchase / spoiled feed</td>
            <td>
              Inventory → open the item → <strong>Correct stock</strong>{" "}
              (Adjustment or Discard against the lot; reason required). Only
              quantities are correctable — a wrong cost, date, or lot number
              can't be fixed yet, so double-check those before saving.
            </td>
          </tr>
          <tr>
            <td>Over- or under-recorded feed usage</td>
            <td>
              Same correction form: a positive Adjustment returns over-used
              stock to the lot (up to what it received); a negative one removes
              under-recorded stock. The usage record itself and its cost
              estimate stay as recorded — corrections fix the stock, not the
              history.
            </td>
          </tr>
          <tr>
            <td>Wrong water record</td>
            <td>
              Water → <strong>correct</strong> on the record — amounts, source,
              meters, and note edit in place (no stock behind water). Flock and
              date are fixed: picked wrong, record it again under the right one.
            </td>
          </tr>
          <tr>
            <td>Wrong numbers in a <em>submitted</em> daily entry</td>
            <td>
              History → <strong>adjust</strong> (admin) — totals, losses,
              mortality, and grade split, with a required reason. Stock and
              the bird ledger update to match automatically, but eggs already
              sold can never be un-counted: shrinking a grade below what was
              sold is refused. The previous values stay visible on the entry.
            </td>
          </tr>
          <tr>
            <td>Entire <em>submitted</em> entry is wrong (wrong flock or day)</td>
            <td>
              History → <strong>void</strong> (admin, reason required): its egg
              lots empty, its deaths are reversed in the bird ledger, and the
              entry is kept as Voided. Refused if any of its eggs were already
              sold — void the sale first. Voiding frees the day: the correct
              entry can then be recorded for the same flock and date.
            </td>
          </tr>
          <tr>
            <td>Mistake in a <em>draft</em> entry or order</td>
            <td>
              Edit it — draft numbers, grade lines, and order lines are all
              editable (draft entries: History → <strong>edit</strong> jumps to
              the Daily entry screen with the day loaded). The flock/date of an
              entry and the customer/date of an order are fixed, though: picked
              wrong, just record it again under the right one (and cancel the
              wrong draft order).
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
            <td>Your own image in place of the Cluckwork mark in the sidebar, uploaded from Farm settings. PNG, JPEG or WebP (2 MB by default), still images only. Stored as a rebuilt copy with camera and location details stripped out.</td></tr>
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
