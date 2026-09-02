import { describe, it, expect, vi, beforeEach } from "vitest";
import { listFlocks, getFlock } from "./cluckwork";
import { apiGet, ApiError } from "./client";

// The screen tests mock this whole module, so none of them exercises the URL
// built below. #512's discovery parameters are only real if the query string
// carries them, so this is the suite that runs the real implementation.
vi.mock("./client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./client")>();
  return { ...actual, apiGet: vi.fn() };
});

const mockGet = vi.mocked(apiGet);

// A scoped 404 from the server (the entity exists but the caller cannot see
// it). The picker's unavailable state must distinguish this from a transport
// failure, so the exact read must propagate ApiError, never swallow it.
const scoped404 = () => new ApiError(404, "NotFound", "The flock was not found.");

beforeEach(() => {
  vi.clearAllMocks();
  mockGet.mockResolvedValue([]);
});

describe("listFlocks query string (#512)", () => {
  it("sends a bare path when no parameter is asked for", async () => {
    await listFlocks();
    expect(mockGet).toHaveBeenCalledWith("/flocks");
  });

  it("carries only the new search parameter when nothing else is asked", async () => {
    await listFlocks({ search: "zulu" });
    expect(mockGet).toHaveBeenCalledWith("/flocks?search=zulu");
  });

  it("keeps the legacy includeArchived=true call byte-for-byte", async () => {
    // /flocks?includeArchived=true must remain exactly what every pre-#512
    // caller sent.
    await listFlocks({ includeArchived: true });
    expect(mockGet).toHaveBeenCalledWith("/flocks?includeArchived=true");
  });

  it("omits includeArchived when it is not true", async () => {
    await listFlocks({ includeArchived: false });
    expect(mockGet).toHaveBeenCalledWith("/flocks");
  });

  it("keeps the legacy limit-only call unchanged", async () => {
    await listFlocks({ limit: 500 });
    expect(mockGet).toHaveBeenCalledWith("/flocks?limit=500");
  });

  it("carries every new parameter in one request", async () => {
    await listFlocks({
      search: "page two",
      eligibility: "all",
      limit: 50,
      offset: 50,
    });
    expect(mockGet).toHaveBeenCalledWith(
      "/flocks?search=page+two&eligibility=all&limit=50&offset=50",
    );
  });

  it("omits offset at the server's own default of zero", async () => {
    // The endpoint floors offset at 0, so omitting it is the same request —
    // matching the existing listCustomers/listBirdMovements convention.
    await listFlocks({ limit: 50, offset: 0 });
    expect(mockGet).toHaveBeenCalledWith("/flocks?limit=50");
  });

  it("encodes search literals so wildcards stay literal on the wire", async () => {
    await listFlocks({ search: "50%_off" });
    expect(mockGet).toHaveBeenCalledWith("/flocks?search=50%25_off");
  });

  it("never serializes eligibility and includeArchived together", async () => {
    // The server 400s the conflicting combination, so the client refuses to
    // build it: eligibility wins, the legacy alias is dropped.
    await listFlocks({ eligibility: "active", includeArchived: true });
    expect(mockGet).toHaveBeenCalledWith("/flocks?eligibility=active");
  });
});

describe("getFlock exact read (#512, US3)", () => {
  it("reads the exact route and nothing else", async () => {
    const flock = { id: "f1" };
    mockGet.mockResolvedValueOnce(flock);
    await expect(getFlock("f1")).resolves.toBe(flock);
    expect(mockGet).toHaveBeenCalledWith("/flocks/f1");
  });

  it("propagates a scoped 404 as the same ApiError (unavailable, not swallowed)", async () => {
    mockGet.mockRejectedValueOnce(scoped404());
    await expect(getFlock("flk-missing")).rejects.toMatchObject({ status: 404 });
    expect(mockGet).toHaveBeenCalledTimes(1);
    expect(mockGet).toHaveBeenCalledWith("/flocks/flk-missing");
  });

  it("propagates a transport error untouched (recovery distinguishes it from a scoped 404)", async () => {
    const boom = new Error("network down");
    mockGet.mockRejectedValueOnce(boom);
    await expect(getFlock("f1")).rejects.toBe(boom);
    expect(mockGet).toHaveBeenCalledTimes(1);
  });

  it("is a GET-only read: a failure never queues a write, and a retry is another GET on the same route", async () => {
    mockGet.mockRejectedValueOnce(scoped404());
    await expect(getFlock("f9")).rejects.toMatchObject({ status: 404 });
    // The caller's recovery (FR-025: retry only exact resolution, never the
    // create) is a second call of the SAME read.
    const flock = { id: "f9" };
    mockGet.mockResolvedValueOnce(flock);
    await expect(getFlock("f9")).resolves.toBe(flock);
    expect(mockGet).toHaveBeenCalledTimes(2);
    expect(mockGet).toHaveBeenNthCalledWith(2, "/flocks/f9");
  });
});
