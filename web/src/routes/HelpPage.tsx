// F18 (#71): in-app user guide + glossary. Content-first and structurally
// boring on purpose — plain headings/paragraphs with existing classes, so the
// Phase 1.5 redesign (#52) restyles it for free. KEEP THIS PAGE CURRENT: the
// docs-sync rule (AGENTS.md) requires every user-visible change to update the
// relevant section here and specs/product/GLOSSARY.md in the same PR.

const TOC = [
  ["daily-loop", "The daily loop"],
  ["daily-entry", "Daily entry"],
  ["flocks", "Flocks & birds"],
  ["grades", "Egg grades"],
  ["stock", "Stock"],
  ["inventory", "Feed & inventory"],
  ["sales", "Customers & sales"],
  ["history", "History"],
  ["mistakes", "Fixing mistakes"],
  ["glossary", "Glossary"],
] as const;

export function HelpPage() {
  return (
    <section className="help">
      <h2>Help</h2>
      <p className="muted">
        How Cluckwork works, screen by screen — and how to undo mistakes.
      </p>

      <nav aria-label="Help contents">
        <ul>
          {TOC.map(([id, label]) => (
            <li key={id}><a href={`#${id}`}>{label}</a></li>
          ))}
        </ul>
      </nav>

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

      <h3 id="daily-entry">Daily entry</h3>
      <ul>
        <li>
          Pick a flock and a date, enter total eggs, losses (cracked / dirty /
          discarded), deaths, and the sellable eggs split across grades. Graded
          quantities can never exceed total minus losses.
        </li>
        <li>
          <strong>Save draft</strong> keeps the day editable. <strong>Submit</strong>{" "}
          makes it official: it creates the day's egg lots and records deaths in
          the flock's bird ledger, and the entry can no longer be edited (an
          admin correction feature is planned).
        </li>
        <li>
          One entry per flock per day. Reopening a day that has a draft loads it
          for editing; if prefill fails, saving is blocked until it succeeds so
          an existing draft is never silently overwritten.
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
          grade from pickers; its existing stock keeps selling until it drains
          and history keeps showing its name.
        </li>
      </ul>

      <h3 id="stock">Stock</h3>
      <ul>
        <li>
          Stock is the sum of your egg lots per grade, split into{" "}
          <strong>available</strong> and <strong>restricted</strong> (eggs under
          a medication withholding period — visible but blocked from sale until
          the date passes).
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
      </ul>

      <h3 id="sales">Customers &amp; sales</h3>
      <ul>
        <li>
          Orders start as <strong>drafts</strong>: add graded lines with
          quantities and prices, edit freely, or <strong>cancel</strong> (the
          draft is kept, read-only).
        </li>
        <li>
          <strong>Confirming</strong> an order allocates real stock — oldest
          lots first — and is the point where inventory changes hands.
        </li>
        <li>
          A mistaken confirm is undone with <strong>Void</strong> (reason
          required): the eggs go back to the exact lots they came from, and the
          order stays listed as Voided. Voiding is for mistakes, not for
          returns of delivered goods.
        </li>
      </ul>

      <h3 id="history">History</h3>
      <ul>
        <li>
          Browse recorded daily entries newest-first, filtered by flock and date
          range. The status column distinguishes Drafts from Submitted entries.
        </li>
      </ul>

      <h3 id="mistakes">Fixing mistakes</h3>
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
            <td>Sales → open the order → <strong>Void order</strong> (stock returns to its lots; reason required)</td>
          </tr>
          <tr>
            <td>Typo in a feed purchase / spoiled feed</td>
            <td>Inventory → open the item → <strong>Correct stock</strong> (Adjustment or Discard against the lot; reason required)</td>
          </tr>
          <tr>
            <td>Over- or under-recorded feed usage</td>
            <td>Same correction form — a positive Adjustment restores over-used stock (up to what the lot received)</td>
          </tr>
          <tr>
            <td>Mistake in a <em>submitted</em> daily entry</td>
            <td><strong>Not yet undoable</strong> — an admin adjust/void feature is planned. Until then, contact your administrator.</td>
          </tr>
          <tr>
            <td>Mistake in a <em>draft</em> entry or order</td>
            <td>Just edit it — drafts are freely editable.</td>
          </tr>
        </tbody>
      </table>

      <h3 id="glossary">Glossary</h3>
      <table className="data">
        <tbody>
          <tr><td><strong>Daily entry</strong></td>
            <td>One flock's day: eggs by grade, losses, deaths. Draft until submitted.</td></tr>
          <tr><td><strong>Egg lot</strong></td>
            <td>A dated batch of sellable eggs of one grade, created by submitting an entry. Stock is the sum of lots.</td></tr>
          <tr><td><strong>Grade</strong></td>
            <td>A grading bucket (size, quality, or custom). Saleable grades can be sold.</td></tr>
          <tr><td><strong>FIFO</strong></td>
            <td>"First in, first out" — sales and feed usage always take the oldest stock first.</td></tr>
          <tr><td><strong>Cull</strong></td>
            <td>Birds deliberately removed from a flock (sold, slaughtered, given away) — not deaths.</td></tr>
          <tr><td><strong>Mortality</strong></td>
            <td>Deaths, recorded on the daily entry; lands in the bird ledger automatically at submit.</td></tr>
          <tr><td><strong>Deplete</strong></td>
            <td>Mark a flock as having no birds left. History stays; reversible via Reactivate.</td></tr>
          <tr><td><strong>Archive</strong></td>
            <td>Hide a finished flock from daily work. Reversible via Reactivate.</td></tr>
          <tr><td><strong>Withdrawal restriction</strong></td>
            <td>A hold on eggs during a medication withholding period — visible in stock, blocked from sale.</td></tr>
          <tr><td><strong>Confirm (order)</strong></td>
            <td>Turns a draft order into a real sale and allocates stock. Undone only by voiding.</td></tr>
          <tr><td><strong>Void (order)</strong></td>
            <td>Undo of a mistaken confirm — stock returns to the exact lots it came from. Needs a reason.</td></tr>
          <tr><td><strong>Cancel (order)</strong></td>
            <td>Close a draft that never happened. No stock involved.</td></tr>
          <tr><td><strong>Inventory item</strong></td>
            <td>A catalog entry for something you stock (feed, supplements…), with a fixed unit of measure.</td></tr>
          <tr><td><strong>Inventory lot</strong></td>
            <td>One received batch of an item, with its own cost. On-hand = sum of lots.</td></tr>
          <tr><td><strong>Movement ledger</strong></td>
            <td>The append-only trail of every stock change. Corrections are new rows, never edits.</td></tr>
          <tr><td><strong>Feed usage</strong></td>
            <td>What a flock ate on a day; drains lots FIFO and estimates cost from them.</td></tr>
          <tr><td><strong>Adjustment / Discard</strong></td>
            <td>Stock corrections against a lot, reason required. Discard = write-off (spoilage).</td></tr>
        </tbody>
      </table>

      <p className="muted">
        Full spec-language definitions live in the repository's{" "}
        <code>specs/product/GLOSSARY.md</code>.
      </p>
    </section>
  );
}
