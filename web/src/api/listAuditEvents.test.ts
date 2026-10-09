import { it, expect, vi, beforeEach } from "vitest";
import { listAuditEvents } from "./cluckwork";
import { apiGet } from "./client";

// AuditPage's tests mock listAuditEvents, so this is the one test that runs the real
// query string the connected-app filters (#800) depend on.
vi.mock("./client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./client")>();
  return { ...actual, apiGet: vi.fn() };
});

const mockGet = vi.mocked(apiGet);

beforeEach(() => {
  vi.clearAllMocks();
  mockGet.mockResolvedValue([]);
});

it("sends connectedAppsOnly for the checkbox", async () => {
  await listAuditEvents({ connectedAppsOnly: true });
  expect(mockGet).toHaveBeenCalledWith("/audit?connectedAppsOnly=true");
});

it("sends both connected-app filters for a one-app view", async () => {
  await listAuditEvents({ connectedAppsOnly: true, connectedAppClientId: "client-a", limit: 50 });
  expect(mockGet).toHaveBeenCalledWith("/audit?connectedAppsOnly=true&connectedAppClientId=client-a&limit=50");
});

it("omits both filters when neither is set", async () => {
  await listAuditEvents({ connectedAppsOnly: false, limit: 50 });
  expect(mockGet).toHaveBeenCalledWith("/audit?limit=50");
});
