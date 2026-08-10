import { describe, it, expect } from "vitest";
import { currentUserId, currentUserRole, currentUserIsAdmin, currentUserMustChangePassword } from "./claims";
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

// #283 — the first-run "must set a new password" claim JwtTokenService adds
// ONLY when true (mirrors the role claims' omit-if-absent shape).
describe("currentUserMustChangePassword", () => {
  it("returns false when no token is stored", () => {
    expect(currentUserMustChangePassword()).toBe(false);
  });

  it("returns false when the claim is absent (the ordinary case)", () => {
    setStoredToken({ sub: "u1", role: "Admin" });
    expect(currentUserMustChangePassword()).toBe(false);
  });

  it("returns true when the claim is the string \"true\"", () => {
    setStoredToken({ sub: "u1", role: "Admin", must_change_password: "true" });
    expect(currentUserMustChangePassword()).toBe(true);
  });

  // The server only ever adds the claim with the literal string "true" (never
  // "false", never a boolean) — anything else must NOT be read as pending, or
  // a malformed/tampered client-side value could wedge a user on the gate.
  it("returns false for any other claim value, not just \"true\"", () => {
    setStoredToken({ sub: "u1", role: "Admin", must_change_password: "yes" });
    expect(currentUserMustChangePassword()).toBe(false);
  });

  it("a malformed token decodes to false, same as the other claim decoders", () => {
    setRawAccessToken("not-a-jwt");
    expect(currentUserMustChangePassword()).toBe(false);
  });
});

// #356 (local review of #492, round 5-10) — the token's standard "sub" claim,
// used by UsersPage's self-target guard so it does not depend on the separate,
// failable /me fetch. Untested until this review caught it: a mutant that
// returned a non-null garbage id on a missing/malformed "sub" survived every
// other test file, and that id feeds a security-adjacent UI decision (hiding
// Disable/Enable on the caller's own row) — a wrong non-null value there is
// the dangerous direction, not just a display glitch.
describe("currentUserId", () => {
  it("returns null when no token is stored", () => {
    expect(currentUserId()).toBeNull();
  });

  it("returns the sub claim when present", () => {
    setStoredToken({ sub: "u1", role: "Admin" });
    expect(currentUserId()).toBe("u1");
  });

  it("returns null when the sub claim is absent", () => {
    setStoredToken({ role: "Admin" });
    expect(currentUserId()).toBeNull();
  });

  it("returns null when the sub claim is an empty string", () => {
    setStoredToken({ sub: "", role: "Admin" });
    expect(currentUserId()).toBeNull();
  });

  it("returns null when the sub claim is not a string", () => {
    setStoredToken({ sub: 12345, role: "Admin" });
    expect(currentUserId()).toBeNull();
  });

  it("a malformed token decodes to null, same as the other claim decoders", () => {
    setRawAccessToken("not-a-jwt");
    expect(currentUserId()).toBeNull();
  });
});
