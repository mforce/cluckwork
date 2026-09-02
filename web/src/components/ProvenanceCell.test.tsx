import { afterEach, beforeEach, describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { ProvenanceCell } from "./ProvenanceCell";
import { FarmContext } from "../farm/FarmContext";
import { account, farmState } from "../test/fixtures";

// #494 — the cell reports the two ends of a record's audit trail. Whether a
// change happened is the SERVER's call: it sends lastChanged* only when the
// latest event differs from the creation event, and null otherwise. So the
// unchanged case arrives as nulls, and the component must never re-derive it by
// comparing timestamps — distinct events can share an instant.
const CREATED = "2026-05-01T08:00:00+00:00";
const CHANGED = "2026-05-03T14:30:00+00:00";

const OFFICIAL = "2026-05-05T09:15:00+00:00";

function renderCell(
  history: Parameters<typeof ProvenanceCell>[0]["history"],
  official?: Parameters<typeof ProvenanceCell>[0]["official"],
) {
  return render(
    <table>
      <tbody>
        <tr>
          <ProvenanceCell history={history} official={official} />
        </tr>
      </tbody>
    </table>,
  );
}

function cell(): HTMLElement {
  const td = document.querySelector("td:not(.muted)");
  if (!td) throw new Error("expected a non-placeholder provenance cell");
  return td as HTMLElement;
}

describe("ProvenanceCell", () => {
  it("shows the creator's actor, not the whole email, when only created", () => {
    renderCell({
      createdByEmail: "ana@farm.test",
      createdAtUtc: CREATED,
      lastChangedByEmail: null,
      lastChangedAtUtc: null,
    });
    expect(screen.getByText(/ana/)).toBeInTheDocument();
    // #653's whole point: the column shrinks by dropping the domain.
    expect(cell().textContent).not.toContain("@");
  });

  it("carries the full creation stamp, in UTC, on the title", () => {
    renderCell({
      createdByEmail: "ana@farm.test",
      createdAtUtc: CREATED,
      lastChangedByEmail: null,
      lastChangedAtUtc: null,
    });
    expect(cell().getAttribute("title")).toBe("Created by ana@farm.test on 2026-05-01 08:00:00");
  });

  it("says nothing about a change on the title when the record has never been changed", () => {
    // The server sends nulls for an unchanged record; repeating the creator
    // would read as an edit that never happened.
    renderCell({
      createdByEmail: "ana@farm.test",
      createdAtUtc: CREATED,
      lastChangedByEmail: null,
      lastChangedAtUtc: null,
    });
    expect(cell().getAttribute("title")).not.toMatch(/Last changed/i);
  });

  it("shows the last changer, not the creator, as the visible actor once the record has been changed", () => {
    renderCell({
      createdByEmail: "ana@farm.test",
      createdAtUtc: CREATED,
      lastChangedByEmail: "bo@farm.test",
      lastChangedAtUtc: CHANGED,
    });
    expect(screen.getByText(/bo/)).toBeInTheDocument();
    expect(screen.queryByText(/^ana/)).not.toBeInTheDocument();
    const title = cell().getAttribute("title") ?? "";
    expect(title).toContain("Created by ana@farm.test on 2026-05-01 08:00:00");
    expect(title).toContain("Last changed by bo@farm.test on 2026-05-03 14:30:00");
  });

  it("still names the actor on a change by the SAME person who created it", () => {
    // Same person, second edit: equality of the EMAIL alone must not suppress
    // the fact from the title — only the instant tells the two events apart.
    renderCell({
      createdByEmail: "ana@farm.test",
      createdAtUtc: CREATED,
      lastChangedByEmail: "ana@farm.test",
      lastChangedAtUtc: CHANGED,
    });
    expect(cell().getAttribute("title")).toContain("Last changed by ana@farm.test");
  });

  it("prefers the change over the creation as the visible actor even in the SAME instant", () => {
    // Two distinct audit events can share a timestamp — the server's queries
    // carry an id tiebreaker precisely because of it, and a seeder running off
    // a fixed clock produces it readily. Telling the two apart by instant alone
    // would call this record untouched and hide a real edit by someone else.
    renderCell({
      createdByEmail: "ana@farm.test",
      createdAtUtc: CREATED,
      lastChangedByEmail: "bo@farm.test",
      lastChangedAtUtc: CREATED,
    });
    expect(screen.getByText(/bo/)).toBeInTheDocument();
  });

  it("renders a placeholder only when there is nothing at all to say", () => {
    renderCell({
      createdByEmail: null,
      createdAtUtc: null,
      lastChangedByEmail: null,
      lastChangedAtUtc: null,
    });
    expect(screen.queryByText(/ana/)).not.toBeInTheDocument();
    const td = screen.getByText("—");
    expect(td.className).toContain("muted");
  });

  it("still reports the change on a record whose creation predates the feature", () => {
    // No backfill, so no creator line — but the change has real attribution and
    // must not go down with it. An earlier revision rendered this whole cell as
    // the placeholder, discarding cy entirely.
    renderCell({
      createdByEmail: null,
      createdAtUtc: null,
      lastChangedByEmail: "cy@farm.test",
      lastChangedAtUtc: CHANGED,
    });
    expect(screen.getByText(/cy/)).toBeInTheDocument();
    expect(cell().getAttribute("title")).not.toMatch(/Created by/i);
  });

  it("keeps the promotion instant off the visible line but on the title (submitted)", () => {
    // #653 collapses the cell to one line; the promotion step has no actor of
    // its own (Daily Entry sends only the instant), so it never drives the
    // visible summary — it stays reachable in the title, same as before.
    renderCell(
      {
        createdByEmail: "ana@farm.test",
        createdAtUtc: CREATED,
        lastChangedByEmail: null,
        lastChangedAtUtc: null,
        madeOfficialAtUtc: OFFICIAL,
      },
      "submitted",
    );
    expect(screen.queryByText(/Submitted/i)).not.toBeInTheDocument();
    expect(cell().getAttribute("title")).toContain("Submitted 2026-05-05 09:15:00");
  });

  it("calls it confirmed on a sales order, not submitted", () => {
    renderCell(
      {
        createdByEmail: "ana@farm.test",
        createdAtUtc: CREATED,
        lastChangedByEmail: null,
        lastChangedAtUtc: null,
        madeOfficialAtUtc: OFFICIAL,
      },
      "confirmed",
    );
    const title = cell().getAttribute("title") ?? "";
    expect(title).toContain("Confirmed 2026-05-05 09:15:00");
    expect(title).not.toContain("Submitted");
  });

  it("stays silent about a promotion step on the title for a resource with no such step", () => {
    // Flocks, egg grades and expenses pass no `official`, so the fact cannot
    // reach the title even if the field somehow arrived — the caller declares
    // whether the concept applies at all.
    renderCell({
      createdByEmail: "ana@farm.test",
      createdAtUtc: CREATED,
      lastChangedByEmail: null,
      lastChangedAtUtc: null,
      madeOfficialAtUtc: OFFICIAL,
    });
    expect(cell().getAttribute("title")).not.toMatch(/Submitted|Confirmed/i);
  });

  it("stays silent on a draft that has not been submitted yet", () => {
    renderCell(
      {
        createdByEmail: "ana@farm.test",
        createdAtUtc: CREATED,
        lastChangedByEmail: null,
        lastChangedAtUtc: null,
        madeOfficialAtUtc: null,
      },
      "submitted",
    );
    expect(cell().getAttribute("title")).not.toMatch(/Submitted/i);
  });

  it("falls back to the bare instant, with no actor, when only a promotion step exists", () => {
    // Only reachable on data old enough to predate #494's creation event
    // entirely, but still promoted since — nothing left to name an actor with.
    renderCell(
      {
        createdByEmail: null,
        createdAtUtc: null,
        lastChangedByEmail: null,
        lastChangedAtUtc: null,
        madeOfficialAtUtc: OFFICIAL,
      },
      "confirmed",
    );
    expect(cell().textContent).not.toContain("·");
  });

  it("never wraps and never becomes the widest column again", () => {
    renderCell({
      createdByEmail: "ana@farm.test",
      createdAtUtc: CREATED,
      lastChangedByEmail: null,
      lastChangedAtUtc: null,
    });
    expect(cell().className).toContain("nowrap");
  });

  describe("on the farm clock", () => {
    beforeEach(() => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date("2026-05-04T12:00:00Z"));
    });
    afterEach(() => {
      vi.useRealTimers();
    });

    it("phrases the visible line on the FARM's timezone, not raw UTC math", () => {
      render(
        <FarmContext.Provider value={farmState({ farm: account({ timeZoneId: "UTC" }) })}>
          <table>
            <tbody>
              <tr>
                <ProvenanceCell
                  history={{
                    createdByEmail: "ana@farm.test",
                    createdAtUtc: CREATED,
                    lastChangedByEmail: null,
                    lastChangedAtUtc: null,
                  }}
                />
              </tr>
            </tbody>
          </table>
        </FarmContext.Provider>,
      );
      // CREATED is 2026-05-01, "now" is faked to 2026-05-04 — 3 farm-local
      // days apart on a UTC farm.
      expect(screen.getByText("3 days ago · ana")).toBeInTheDocument();
    });
  });
});
