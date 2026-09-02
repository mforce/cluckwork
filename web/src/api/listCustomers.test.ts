import { describe, it, expect, vi, beforeEach } from "vitest";
import { listCustomers, getCustomer } from "./cluckwork";
import { apiGet, ApiError } from "./client";

// The screen tests mock this whole module, so none of them exercises the URL
// built below. #511's offset support is only real if the query string carries
// it, so this is the one test that runs the real implementation.
vi.mock("./client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./client")>();
  return { ...actual, apiGet: vi.fn() };
});

const mockGet = vi.mocked(apiGet);

beforeEach(() => {
  vi.clearAllMocks();
  mockGet.mockResolvedValue([]);
});

describe("listCustomers query string (#511)", () => {
  it("carries both limit and offset when paging past the first page", async () => {
    await listCustomers({ limit: 100, offset: 100 });
    expect(mockGet).toHaveBeenCalledWith("/customers?limit=100&offset=100");
  });

  it("omits offset at the server's own default of zero", async () => {
    // The endpoint does Math.Max(offset ?? 0, 0), so omitting 0 is the same
    // request — and it matches how listBirdMovements/listInventoryMovements
    // already build theirs.
    await listCustomers({ limit: 100, offset: 0 });
    expect(mockGet).toHaveBeenCalledWith("/customers?limit=100");
  });

  it("sends a bare path when no paging is asked for", async () => {
    await listCustomers();
    expect(mockGet).toHaveBeenCalledWith("/customers");
  });
});

describe("listCustomers search (#512)", () => {
  it("carries the search parameter alongside paging", async () => {
    await listCustomers({ search: "page two", limit: 50, offset: 50 });
    expect(mockGet).toHaveBeenCalledWith(
      "/customers?search=page+two&limit=50&offset=50",
    );
  });

  it("encodes search literals so wildcards stay literal on the wire", async () => {
    await listCustomers({ search: "50%_off" });
    expect(mockGet).toHaveBeenCalledWith("/customers?search=50%25_off");
  });

  it("sends only the search when nothing else is asked", async () => {
    await listCustomers({ search: "zulu" });
    expect(mockGet).toHaveBeenCalledWith("/customers?search=zulu");
  });
});

describe("getCustomer exact read (#512, US3)", () => {
  it("reads the exact route and nothing else", async () => {
    const customer = { id: "c1" };
    mockGet.mockResolvedValueOnce(customer);
    await expect(getCustomer("c1")).resolves.toBe(customer);
    expect(mockGet).toHaveBeenCalledWith("/customers/c1");
  });

  it("propagates a scoped 404 as the same ApiError (unavailable, not swallowed)", async () => {
    mockGet.mockRejectedValueOnce(new ApiError(404, "NotFound", "The customer was not found."));
    await expect(getCustomer("cus-missing")).rejects.toMatchObject({ status: 404 });
    expect(mockGet).toHaveBeenCalledTimes(1);
    expect(mockGet).toHaveBeenCalledWith("/customers/cus-missing");
  });

  it("propagates a transport error untouched", async () => {
    const boom = new Error("network down");
    mockGet.mockRejectedValueOnce(boom);
    await expect(getCustomer("c1")).rejects.toBe(boom);
    expect(mockGet).toHaveBeenCalledTimes(1);
  });

  it("is a GET-only read: a retry after failure repeats the same GET", async () => {
    mockGet.mockRejectedValueOnce(new ApiError(404, "NotFound", "The customer was not found."));
    await expect(getCustomer("c2")).rejects.toMatchObject({ status: 404 });
    const customer = { id: "c2" };
    mockGet.mockResolvedValueOnce(customer);
    await expect(getCustomer("c2")).resolves.toBe(customer);
    expect(mockGet).toHaveBeenCalledTimes(2);
    expect(mockGet).toHaveBeenNthCalledWith(2, "/customers/c2");
  });
});
