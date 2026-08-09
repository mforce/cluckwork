import { describe, it, expect } from "vitest";
import { errText } from "./errText";
import { ApiError } from "../api/client";

// Extracted from a dozen screens in #469 so usePagedList could share it. The
// three branches are the three things a rejected fetch actually hands us.
describe("errText", () => {
  it("prefers an ApiError's server-supplied message", () => {
    expect(errText(new ApiError(422, "Unprocessable", "Stock is insufficient.")))
      .toBe("Stock is insufficient.");
  });

  it("falls back to a plain Error's message", () => {
    expect(errText(new Error("network down"))).toBe("network down");
  });

  it("stringifies a non-Error rejection rather than rendering [object Object]", () => {
    // A rejected promise can carry anything; the string is what the banner
    // shows, so it must never be empty or "undefined".
    expect(errText("boom")).toBe("boom");
    expect(errText(503)).toBe("503");
  });
});
