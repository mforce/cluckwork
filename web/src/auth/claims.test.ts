import { describe, it, expect } from "vitest";
import { currentUserRole, currentUserIsAdmin } from "./claims";
import { setStoredToken, setRawAccessToken } from "../test/jwt";

// localStorage is reset after every test in src/test/setup.ts (single source of
// truth) — no per-file cleanup here.

describe("currentUserRole", () => {
  it("returns Worker when no token is stored", () => {
    expect(currentUserRole()).toBe("Worker");
  });

  it("returns Worker when the payload carries no role claim", () => {
    setStoredToken({ sub: "u1" });
    expect(currentUserRole()).toBe("Worker");
  });

  it.each([
    ["Admin"],
    ["Manager"],
    ["Sales"],
    ["ReadOnly"],
  ])("decodes a single %s role claim", (role) => {
    setStoredToken({ sub: "u1", role });
    expect(currentUserRole()).toBe(role);
  });

  it("empty-string role claims are ignored → Worker", () => {
    setStoredToken({ sub: "u1", role: "" });
    expect(currentUserRole()).toBe("Worker");
  });

  describe("highest-role precedence for multi-role principals (mirrors the F27 backend rule)", () => {
    it("Sales + ReadOnly → Sales", () => {
      setStoredToken({ sub: "u1", role: ["ReadOnly", "Sales"] });
      expect(currentUserRole()).toBe("Sales");
    });

    it("ReadOnly + Admin → Admin regardless of order", () => {
      setStoredToken({ sub: "u1", role: ["ReadOnly", "Admin"] });
      expect(currentUserRole()).toBe("Admin");
    });

    // The pair the AuthPolicies comment warns about: lowest-wins would let this
    // resolve to Sales and mis-gate production. Must be Manager.
    it("Manager + Sales → Manager", () => {
      setStoredToken({ sub: "u1", role: ["Sales", "Manager"] });
      expect(currentUserRole()).toBe("Manager");
    });
  });

  describe("unknown roles are Denied, not Worker", () => {
    it("an unrecognized-only role claim → Denied", () => {
      setStoredToken({ sub: "u1", role: "Contractor" });
      expect(currentUserRole()).toBe("Denied");
    });

    it("multiple unrecognized roles → Denied", () => {
      setStoredToken({ sub: "u1", role: ["Contractor", "Auditor"] });
      expect(currentUserRole()).toBe("Denied");
    });

    it("a known role alongside an unknown one still resolves to the known role", () => {
      setStoredToken({ sub: "u1", role: ["Contractor", "Sales"] });
      expect(currentUserRole()).toBe("Sales");
    });
  });

  // A malformed/unreadable token decodes to Worker — the same tier as a logged-
  // out user (currentUserRole has no distinct "invalid session" state today).
  // This characterizes current behavior; whether a corrupt-but-present token
  // should instead force re-auth (Denied) is tracked as a follow-up, and the
  // API rejects the bad token regardless of what the UI shows.
  describe("malformed tokens decode to Worker (characterization)", () => {
    it("a non-JWT access token → Worker", () => {
      setRawAccessToken("not-a-jwt");
      expect(currentUserRole()).toBe("Worker");
    });

    it("a payload segment that is not valid base64/JSON → Worker", () => {
      setRawAccessToken("aaa.!!!not-json!!!.sig");
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
    setStoredToken({ sub: "u1", role });
    expect(currentUserIsAdmin()).toBe(expected);
  });

  it("no token (Worker) → false", () => {
    expect(currentUserIsAdmin()).toBe(false);
  });
});
