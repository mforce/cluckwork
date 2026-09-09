import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import { useState } from "react";
import { FlockPicker } from "./FlockPicker";
import type { Flock } from "../api/cluckwork";
import { listFlocks } from "../api/cluckwork";

// #735 — opening the picker from its trigger must hand the keyboard to the
// search input and select the committed name, so the first keystroke starts
// a search instead of appending to "Sim House A". Before this, the only
// `focus()` calls in the engine were the two Retry paths; a pointer open left
// the input blurred with the old name in it, indistinguishable from a row.
vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return { ...actual, listFlocks: vi.fn() };
});

const F = (id: string, name: string): Flock => ({
  id, farmId: "farm1", houseId: "h1", name, breed: "Lohmann", placementDate: "2026-01-01",
  initialCount: 100, currentBirds: 90, status: "Active",
  createdByEmail: null, createdAtUtc: null, lastChangedByEmail: null, lastChangedAtUtc: null,
});

beforeEach(() => {
  vi.mocked(listFlocks).mockResolvedValue([F("a1", "Sim House A"), F("a2", "Sim House B")]);
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

function Harness() {
  const [open, setOpen] = useState(false);
  return (
    <FlockPicker label="Flock" eligibility="active" open={open}
      controlledCommitted={F("a1", "Sim House A")} controlledGeneration={1}
      onEscape={() => setOpen(false)} onOutsideClick={() => setOpen(false)}
      trigger={<button type="button" onClick={() => setOpen(true)}>Sim House A</button>} />
  );
}

describe("#735 open focus", () => {
  it("opening from the trigger focuses the input and selects the committed name", async () => {
    render(<Harness />);
    fireEvent.click(screen.getByText("Sim House A", { selector: "button *, button" }).closest("button")!);
    const input = await screen.findByRole<HTMLInputElement>("combobox");
    await waitFor(() => expect(input.value).toBe("Sim House A"));
    expect(document.activeElement).toBe(input);
    expect(input.selectionStart).toBe(0);
    expect(input.selectionEnd).toBe(input.value.length);
  });
});
