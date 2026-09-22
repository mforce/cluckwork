import { describe, it, expect, vi, afterEach } from "vitest";
import { describeVersionChange, fetchAvailableVersion } from "./appVersion";

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("fetchAvailableVersion (#936)", () => {
  it("reads the version from a successful, well-formed response", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ version: "1.2.3" }), { status: 200 }),
    ));
    await expect(fetchAvailableVersion()).resolves.toBe("1.2.3");
  });

  it("requests with cache: no-store, so it never reads the active worker's own cached copy", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ version: "1.2.3" }), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    await fetchAvailableVersion();
    expect(fetchMock).toHaveBeenCalledWith("/version.json", expect.objectContaining({ cache: "no-store" }));
  });

  it("returns null on a non-2xx response (dev/test builds never emit version.json)", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("not found", { status: 404 })));
    await expect(fetchAvailableVersion()).resolves.toBeNull();
  });

  it("returns null when the payload is not JSON", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("<html>not json</html>", { status: 200 })));
    await expect(fetchAvailableVersion()).resolves.toBeNull();
  });

  it("returns null when the payload is missing the version field", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({}), { status: 200 })));
    await expect(fetchAvailableVersion()).resolves.toBeNull();
  });

  it("returns null when version is present but not a non-empty string", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({ version: "" }), { status: 200 })));
    await expect(fetchAvailableVersion()).resolves.toBeNull();
  });

  it("returns null when the network read throws", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("offline")));
    await expect(fetchAvailableVersion()).resolves.toBeNull();
  });
});

describe("describeVersionChange (#936)", () => {
  it("describes the change when current and available genuinely differ", () => {
    expect(describeVersionChange("0.1.2", "0.2.0")).toEqual({ current: "0.1.2", available: "0.2.0" });
  });

  it("shows nothing when this build's own version is unknown (dev/test builds)", () => {
    expect(describeVersionChange(undefined, "0.2.0")).toBeNull();
  });

  it("shows nothing when the handshake produced no available version", () => {
    expect(describeVersionChange("0.1.2", null)).toBeNull();
  });

  it("shows nothing when current and available happen to already agree", () => {
    expect(describeVersionChange("0.1.2", "0.1.2")).toBeNull();
  });
});
