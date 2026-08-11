import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { ProvenanceCell } from "./ProvenanceCell";

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

describe("ProvenanceCell", () => {
  it("names the creator and when", () => {
    renderCell({
      createdByEmail: "ana@farm.test",
      createdAtUtc: CREATED,
      lastChangedByEmail: null,
      lastChangedAtUtc: null,
    });
    expect(screen.getByText(/ana@farm\.test/)).toBeInTheDocument();
  });

  it("says nothing about a change when the record has never been changed", () => {
    // The server sends nulls for an unchanged record; repeating the creator
    // would read as an edit that never happened.
    renderCell({
      createdByEmail: "ana@farm.test",
      createdAtUtc: CREATED,
      lastChangedByEmail: null,
      lastChangedAtUtc: null,
    });
    expect(screen.queryByText(/Last changed/i)).not.toBeInTheDocument();
  });

  it("names the last changer separately once the record has been changed", () => {
    renderCell({
      createdByEmail: "ana@farm.test",
      createdAtUtc: CREATED,
      lastChangedByEmail: "bo@farm.test",
      lastChangedAtUtc: CHANGED,
    });
    expect(screen.getByText(/ana@farm\.test/)).toBeInTheDocument();
    expect(screen.getByText(/bo@farm\.test/)).toBeInTheDocument();
  });

  it("shows a change by the SAME actor at a later time", () => {
    // Same person, second edit: equality of the EMAIL alone must not suppress
    // the line — only the instant tells the two events apart.
    renderCell({
      createdByEmail: "ana@farm.test",
      createdAtUtc: CREATED,
      lastChangedByEmail: "ana@farm.test",
      lastChangedAtUtc: CHANGED,
    });
    expect(screen.getByText(/Last changed/i)).toBeInTheDocument();
  });

  it("shows a change made in the SAME instant as the creation", () => {
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
    expect(screen.getByText(/bo@farm\.test/)).toBeInTheDocument();
  });

  it("renders a placeholder only when there is nothing at all to say", () => {
    renderCell({
      createdByEmail: null,
      createdAtUtc: null,
      lastChangedByEmail: null,
      lastChangedAtUtc: null,
    });
    expect(screen.queryByText(/Created by/i)).not.toBeInTheDocument();
    expect(screen.getByText("—")).toBeInTheDocument();
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
    expect(screen.getByText(/cy@farm\.test/)).toBeInTheDocument();
    expect(screen.queryByText(/Created by/i)).not.toBeInTheDocument();
    expect(screen.queryByText("—")).not.toBeInTheDocument();
  });

  // #494 — the promotion instant. A self-submit is excluded from "last changed
  // by", so without this line the moment stock was minted would appear nowhere
  // on the record's own page.
  it("reports when a daily entry was submitted, even with no change to report", () => {
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
    expect(screen.getByText(/Submitted 2026-05-05 09:15:00/)).toBeInTheDocument();
    expect(screen.queryByText(/Last changed/i)).not.toBeInTheDocument();
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
    expect(screen.getByText(/Confirmed 2026-05-05 09:15:00/)).toBeInTheDocument();
    expect(screen.queryByText(/Submitted/i)).not.toBeInTheDocument();
  });

  it("stays silent on a resource with no promotion step", () => {
    // Flocks, egg grades and expenses pass no `official`, so the line cannot
    // render even if the field somehow arrived — the caller declares whether
    // the concept applies at all.
    renderCell({
      createdByEmail: "ana@farm.test",
      createdAtUtc: CREATED,
      lastChangedByEmail: null,
      lastChangedAtUtc: null,
      madeOfficialAtUtc: OFFICIAL,
    });
    expect(screen.queryByText(/Submitted|Confirmed/i)).not.toBeInTheDocument();
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
    expect(screen.queryByText(/Submitted/i)).not.toBeInTheDocument();
  });
});
