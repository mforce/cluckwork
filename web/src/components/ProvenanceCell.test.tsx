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

function renderCell(history: Parameters<typeof ProvenanceCell>[0]["history"]) {
  return render(
    <table>
      <tbody>
        <tr>
          <ProvenanceCell history={history} />
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

  it("renders a placeholder for a record that predates the feature", () => {
    // No backfill: all four arrive null together and there is nothing to claim.
    renderCell({
      createdByEmail: null,
      createdAtUtc: null,
      lastChangedByEmail: null,
      lastChangedAtUtc: null,
    });
    expect(screen.queryByText(/Created by/i)).not.toBeInTheDocument();
    expect(screen.getByText("—")).toBeInTheDocument();
  });
});
