import { describe, it, expect, vi } from "vitest";
import { resetUserTwoFactor } from "./cluckwork";
import { apiDelete, STEP_UP_HEADER } from "./client";

// The Users page tests mock this module, so this is the one test that runs
// the real request builder (#1280).
vi.mock("./client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./client")>();
  return { ...actual, apiDelete: vi.fn() };
});

describe("resetUserTwoFactor (#1280)", () => {
  it("sends a DELETE to the user's two-factor route with the key and the step-up grant", async () => {
    await resetUserTwoFactor("u-e", "key-1", "grant-1");
    expect(vi.mocked(apiDelete)).toHaveBeenCalledWith("/users/u-e/two-factor", "key-1", { [STEP_UP_HEADER]: "grant-1" });
  });
});
