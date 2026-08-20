import { describe, expect, it } from "vitest";
import { resolveApiTarget, resolveDevServer } from "../../vite.config";

describe("resolveDevServer", () => {
  it("uses the standalone Vite defaults when Aspire has not supplied PORT", () => {
    expect(resolveDevServer({})).toEqual({ port: 5173, strictPort: false });
  });

  it("uses Aspire's supplied PORT and refuses an alternate port", () => {
    expect(resolveDevServer({ PORT: "43210" })).toEqual({ port: 43210, strictPort: true });
  });

  it.each([
    ["0"],
    ["65536"],
    ["not-a-port"],
    ["43210.5"],
    ["+43210"],
    ["-43210"],
    [" 43210"],
    ["43210 "],
    [""],
  ])("rejects invalid PORT value %j", (port) => {
    expect(() => resolveDevServer({ PORT: port })).toThrow(/PORT/);
  });
});

describe("resolveApiTarget", () => {
  it("prefers a process-level VITE_API_TARGET over file environment values", () => {
    expect(
      resolveApiTarget(
        { VITE_API_TARGET: "http://process.example:8081" },
        { VITE_API_TARGET: "http://file.example:8082" },
      ),
    ).toBe("http://process.example:8081");
  });

  it("uses a file environment VITE_API_TARGET when the process has none", () => {
    expect(resolveApiTarget({}, { VITE_API_TARGET: "http://file.example:8082" })).toBe(
      "http://file.example:8082",
    );
  });

  it("retains the local API fallback when neither source provides a target", () => {
    expect(resolveApiTarget({}, {})).toBe("http://localhost:8080");
  });
});
