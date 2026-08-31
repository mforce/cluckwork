import { describe, it, expect, vi, beforeEach } from "vitest";
import { listCustomers } from "./cluckwork";
import { apiGet } from "./client";

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
