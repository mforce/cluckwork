import { describe, expect, it } from "vitest";
import { returnPath } from "./returnPath";

describe("returnPath (#798)", () => {
  it.each([
    ["https://evil.example/connect", ""],
    ["//evil.example", ""],
    ["//evil.example/connect", "?client_id=x"],
    ["/\\evil.example", ""],
    ["\\\\evil.example", ""],
    ["https:evil.example", ""],
  ])("refuses %s, which leaves this origin", (pathname, search) => {
    expect(returnPath({ pathname, search })).toBe("/");
  });

  it("keeps the consent route's request", () => {
    expect(returnPath({ pathname: "/connect", search: "?client_id=abc&scope=farm%3Aread" }))
      .toBe("/connect?client_id=abc&scope=farm%3Aread");
  });

  it("returns any other path without its query, as before", () => {
    expect(returnPath({ pathname: "/sales", search: "?x=1" })).toBe("/sales");
  });

  it("goes home with nowhere to return to", () => {
    expect(returnPath(undefined)).toBe("/");
  });
});
