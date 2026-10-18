import { beforeEach, describe, expect, it, vi } from "vitest";
import { approveConsent } from "./oauth";
import { apiFetch, STEP_UP_HEADER } from "./client";

// ConnectPage's tests mock this module, so this suite pins what reaches the wire.
vi.mock("./client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./client")>()),
  apiFetch: vi.fn(),
}));

const SEARCH = "?client_id=c1&scope=farm%3Aread%20daily-entries%3Awrite&state=s";

beforeEach(() => {
  vi.mocked(apiFetch).mockReset().mockResolvedValue({ redirectUri: "http://127.0.0.1/cb" });
});

describe("approveConsent (#1326)", () => {
  it.each([[["daily-entries:write"], '["daily-entries:write"]'], [[], "[]"]])(
    "keeps the OAuth query as it is and sends %j as a JSON array",
    async (chosen, header) => {
      await approveConsent(SEARCH, "grant-1", chosen);

      expect(apiFetch).toHaveBeenCalledWith(`/oauth/authorize${SEARCH}`, {
        method: "GET", headers: { [STEP_UP_HEADER]: "grant-1", "X-Cluckwork-Consent-Scopes": header },
      });
    });
});
