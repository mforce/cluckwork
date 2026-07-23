import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { uploadFarmLogo } from "./cluckwork";
import { setAccessToken, clearAccessToken } from "../auth/tokenStore";

// The one wrapper with logic of its own (#123): everything else in cluckwork.ts
// is a path and a type. Exercised against a stubbed fetch, so the real
// apiPutBytes → apiFetch → raw chain runs — the screen tests mock this module
// out and never evaluate it.
let fetchMock: ReturnType<typeof vi.fn>;

function headerOf(name: string): string | null {
  const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
  return new Headers(init.headers).get(name);
}

beforeEach(() => {
  fetchMock = vi.fn().mockResolvedValue(
    new Response(JSON.stringify({ contentHash: "abc" }), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    }));
  vi.stubGlobal("fetch", fetchMock);
  setAccessToken("token");
});

afterEach(() => {
  vi.unstubAllGlobals();
  clearAccessToken();
});

describe("uploadFarmLogo", () => {
  it("PUTs the raw bytes to /account/logo under the file's own type", async () => {
    const file = new File(["png-bytes"], "logo.png", { type: "image/png" });

    await uploadFarmLogo(file, "logo-key");

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/account/logo");
    expect(init.method).toBe("PUT");
    expect(init.body).toBe(file); // not JSON-encoded, not multipart
    expect(headerOf("Content-Type")).toBe("image/png");
    expect(headerOf("Idempotency-Key")).toBe("logo-key");
  });

  it("still uploads when the browser could not guess a type", async () => {
    // A File dragged from some sources carries an empty `type`. An empty
    // Content-Type header is not a valid request, and the server sniffs the
    // bytes anyway — so the declared type is a formality that must still be
    // filled in rather than left blank.
    const file = new File(["png-bytes"], "logo", { type: "" });

    await uploadFarmLogo(file, "logo-key");

    expect(headerOf("Content-Type")).toBe("application/octet-stream");
  });
});
