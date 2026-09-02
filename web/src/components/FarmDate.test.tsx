import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { FarmContext } from "../farm/FarmContext";
import { FarmDate } from "./FarmDate";
import { account, farmState } from "../test/fixtures";

// #650 — a list date is a <time> carrying the ISO value, so a locale-formatted
// label ("14/08/2026") never hides what day it is from a script or a screen
// reader that reads datetime, and the E2E harness can select a row by the
// farm's own day without knowing the farm's locale.
describe("FarmDate", () => {
  it("renders the farm-formatted label inside a <time> whose datetime is the ISO day", () => {
    render(
      <FarmContext.Provider value={farmState({ farm: account({ locale: "es-MX" }) })}>
        <FarmDate iso="2026-08-14" />
      </FarmContext.Provider>,
    );
    const time = screen.getByText("14/08/2026");
    expect(time.tagName).toBe("TIME");
    expect(time).toHaveAttribute("datetime", "2026-08-14");
  });
});
