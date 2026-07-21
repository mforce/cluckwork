import { describe, it, expect, vi } from "vitest";
import { renderHook } from "@testing-library/react";
import { useAuth } from "./useAuth";

// AuthContext defaults to null (AuthContext.tsx), so a consumer rendered outside
// <AuthProvider> hits the guard. Pinning it keeps the documented invariant real
// and covers useAuth's throw branch (otherwise permanently 0-hit, since every
// other test renders through renderWithProviders / AuthProvider).
describe("useAuth", () => {
  it("throws a clear error when called outside an AuthProvider", () => {
    // React re-logs a render error to console.error; silence it so this
    // deliberate throw doesn't print a scary stack in a passing run.
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    expect(() => renderHook(() => useAuth())).toThrow(
      "useAuth must be used within <AuthProvider>",
    );
    spy.mockRestore();
  });
});
