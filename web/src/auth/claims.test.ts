import { describe, it, expect, beforeEach } from "vitest";
import { currentUserRole, currentUserIsAdmin } from "./claims";

// tokenStore persists under this key (src/auth/tokenStore.ts). The decode reads
// the access token's payload segment only; header + signature are ignored.
const TOKEN_KEY = "cluckwork.tokens";

// Build a JWT-shaped string with the given payload. Real tokens are base64url
// (‑/_ instead of +//, no padding) — encode the same way so the test exercises
// claims.ts's url-safe → standard reversal, not just plain base64.
function makeToken(payload: Record<string, unknown>): string {
  const b64url = (o: unknown) =>
    btoa(JSON.stringify(o)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  return `${b64url({ alg: "HS256", typ: "JWT" })}.${b64url(payload)}.signature`;
}

function setToken(payload: Record<string, unknown>): void {
  localStorage.setItem(
    TOKEN_KEY,
    JSON.stringify({ accessToken: makeToken(payload), refreshToken: "r", expiresAt: "2099-01-01T00:00:00Z" }),
  );
}

beforeEach(() => localStorage.clear());

describe("currentUserRole", () => {
  it("returns Worker when no token is stored", () => {
    expect(currentUserRole()).toBe("Worker");
  });

  it("returns Worker when the payload carries no role claim", () => {
    setToken({ sub: "u1" });
    expect(currentUserRole()).toBe("Worker");
  });

  it.each([
    ["Admin"],
    ["Manager"],
    ["Sales"],
    ["ReadOnly"],
  ])("decodes a single %s role claim", (role) => {
    setToken({ sub: "u1", role });
    expect(currentUserRole()).toBe(role);
  });

  it("empty-string role claims are ignored → Worker", () => {
    setToken({ sub: "u1", role: "" });
    expect(currentUserRole()).toBe("Worker");
  });

  describe("highest-role precedence for multi-role principals (mirrors the F27 backend rule)", () => {
    it("Sales + ReadOnly → Sales", () => {
      setToken({ sub: "u1", role: ["ReadOnly", "Sales"] });
      expect(currentUserRole()).toBe("Sales");
    });

    it("ReadOnly + Admin → Admin regardless of order", () => {
      setToken({ sub: "u1", role: ["ReadOnly", "Admin"] });
      expect(currentUserRole()).toBe("Admin");
    });

    // The pair the AuthPolicies comment warns about: lowest-wins would let this
    // resolve to Sales and mis-gate production. Must be Manager.
    it("Manager + Sales → Manager", () => {
      setToken({ sub: "u1", role: ["Sales", "Manager"] });
      expect(currentUserRole()).toBe("Manager");
    });
  });

  describe("unknown roles are Denied, not Worker", () => {
    it("an unrecognized-only role claim → Denied", () => {
      setToken({ sub: "u1", role: "Contractor" });
      expect(currentUserRole()).toBe("Denied");
    });

    it("multiple unrecognized roles → Denied", () => {
      setToken({ sub: "u1", role: ["Contractor", "Auditor"] });
      expect(currentUserRole()).toBe("Denied");
    });

    it("a known role alongside an unknown one still resolves to the known role", () => {
      setToken({ sub: "u1", role: ["Contractor", "Sales"] });
      expect(currentUserRole()).toBe("Sales");
    });
  });

  describe("malformed tokens fail closed to Worker", () => {
    it("a non-JWT access token → Worker", () => {
      localStorage.setItem(TOKEN_KEY, JSON.stringify({ accessToken: "not-a-jwt", refreshToken: "r", expiresAt: "x" }));
      expect(currentUserRole()).toBe("Worker");
    });

    it("a payload segment that is not valid base64/JSON → Worker", () => {
      localStorage.setItem(TOKEN_KEY, JSON.stringify({ accessToken: "aaa.!!!not-json!!!.sig", refreshToken: "r", expiresAt: "x" }));
      expect(currentUserRole()).toBe("Worker");
    });

    it("corrupt token-store JSON → Worker", () => {
      localStorage.setItem(TOKEN_KEY, "{not json");
      expect(currentUserRole()).toBe("Worker");
    });
  });
});

describe("currentUserIsAdmin", () => {
  it.each([
    ["Admin", true],
    ["Manager", true],
    ["Sales", false],
    ["ReadOnly", false],
    ["Contractor", false], // Denied
  ] as const)("%s → %s", (role, expected) => {
    setToken({ sub: "u1", role });
    expect(currentUserIsAdmin()).toBe(expected);
  });

  it("no token (Worker) → false", () => {
    expect(currentUserIsAdmin()).toBe(false);
  });
});
