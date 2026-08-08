import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { useState } from "react";
import { StepperUnitSelector } from "./StepperUnitSelector";
import { MeContext, MeUpdateContext } from "./SessionContext";
import { FarmContext } from "../farm/FarmContext";
import { listEggUnitConversions, putMeStepperUnit } from "../api/cluckwork";
import type { Me } from "../api/cluckwork";
import { account, farmState } from "../test/fixtures";
// i18n side-effect import: registers the catalogs so the label text resolves.
import "../i18n";

vi.mock("../api/cluckwork", async (io) => ({
  ...(await io<typeof import("../api/cluckwork")>()),
  listEggUnitConversions: vi.fn(),
  putMeStepperUnit: vi.fn(),
}));

const mockList = vi.mocked(listEggUnitConversions);
const mockPut = vi.mocked(putMeStepperUnit);

const ME: Me = {
  id: "u1", email: "a@b.co", name: null, role: "Admin", language: null,
  preferredStepperUnit: null,
};

// A live host, not a static provider: the selector applies its change through
// useMeUpdate() and reads it back through useMe(), so the test host has to
// actually route the patch — a static value would leave the select stuck.
function Host({ me = ME }: { me?: Me } = {}) {
  const [current, setCurrent] = useState(me);
  return (
    <MeContext.Provider value={current}>
      <MeUpdateContext.Provider value={(patch) => setCurrent((prev) => ({ ...prev, ...patch }))}>
        <FarmContext.Provider value={farmState({ farm: account({ defaultStepperUnit: "Dozen" }) })}>
          <StepperUnitSelector />
        </FarmContext.Provider>
      </MeUpdateContext.Provider>
    </MeContext.Provider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mockList.mockResolvedValue([
    { id: "c1", unitCode: "Individual", eggsPerUnit: 1, active: true, version: 0 },
    { id: "c3", unitCode: "Tray", eggsPerUnit: 30, active: true, version: 0 },
    { id: "c4", unitCode: "Case", eggsPerUnit: 360, active: false, version: 0 },
  ]);
  mockPut.mockResolvedValue(undefined);
});

describe("StepperUnitSelector (#444)", () => {
  it("offers the farm default plus every ACTIVE unit, with the override selected", async () => {
    render(<Host me={{ ...ME, preferredStepperUnit: "Tray" }} />);
    const select = screen.getByLabelText("Daily Entry counting unit");
    await waitFor(() =>
      expect(screen.getByRole("option", { name: "Tray" })).toBeInTheDocument());

    expect(select).toHaveValue("Tray");
    // Farm default names the CURRENT farm value; inactive Case is not offered.
    expect(screen.getByRole("option", { name: "Farm default (Dozen)" })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Case" })).toBeNull();
  });

  it("persists a picked unit and applies it to MeContext optimistically", async () => {
    render(<Host />);
    const select = screen.getByLabelText("Daily Entry counting unit");
    await waitFor(() =>
      expect(screen.getByRole("option", { name: "Tray" })).toBeInTheDocument());

    fireEvent.change(select, { target: { value: "Tray" } });

    expect(select).toHaveValue("Tray"); // applied through the patch, not local state
    expect(mockPut).toHaveBeenCalledWith("Tray");
  });

  it("clears the override (null on the wire) when the farm-default option is picked", async () => {
    render(<Host me={{ ...ME, preferredStepperUnit: "Tray" }} />);
    const select = screen.getByLabelText("Daily Entry counting unit");
    await waitFor(() =>
      expect(screen.getByRole("option", { name: "Tray" })).toBeInTheDocument());

    fireEvent.change(select, { target: { value: "" } });

    expect(select).toHaveValue("");
    // null, not "Dozen": the preference FOLLOWS the farm default rather than
    // freezing whatever it happens to be today.
    expect(mockPut).toHaveBeenCalledWith(null);
  });
});
