import { useState } from "react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act, render } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { UsersPage } from "./UsersPage";
import { Login } from "./Login";
import { ProtectedRoute } from "./ProtectedRoute";
import { renderWithProviders } from "../test/renderWithProviders";
import { farmState, NO_RECORD_HISTORY } from "../test/fixtures";
import { AuthContext } from "../auth/AuthContext";
import type { Role } from "../auth/claims";
import { FarmContext } from "../farm/FarmContext";
import { MeContext } from "../session/SessionContext";
import type { Me } from "../api/cluckwork";
import {
  assignFlock, changeUserEmail, changeUserRole, createUser, disableUser, enableUser, listFlockAssignments, listFlocks,
  listUsers, setUserPassword, unassignFlock, updateUser,
} from "../api/cluckwork";
import type { Flock, FlockAssignment, User } from "../api/cluckwork";
import { apiGet, ApiError, stepUp } from "../api/client";
import { getAccessToken } from "../auth/tokenStore";
import i18n from "../i18n";

// Runtime-generated, with NO static substring — GitGuardian's scanner
// flagged an earlier version of this line even though it was already
// randomized, because the readable "password-shaped" prefix it was appended
// to (`Own3r!${...}`) was enough to trigger on its own. One shared value:
// these tests assert the typed proof password is the one SENT to stepUp(),
// so identity is what matters, not content or shape.
const OWNER_STEP_UP_PASSWORD = crypto.randomUUID();

// Network seam only; ApiError stays real (errText branches on `instanceof`).
vi.mock("../api/cluckwork", () => ({
  listUsers: vi.fn(),
  createUser: vi.fn(),
  updateUser: vi.fn(),
  setUserPassword: vi.fn(),
  changeUserRole: vi.fn(),
  changeUserEmail: vi.fn(),
  disableUser: vi.fn(),
  enableUser: vi.fn(),
  listFlockAssignments: vi.fn(),
  assignFlock: vi.fn(),
  unassignFlock: vi.fn(),
  listFlocks: vi.fn(),
}));

// #308 — only stepUp is mocked; ApiError/STEP_UP_HEADER/apiPost etc. stay real
// (errText branches on `instanceof ApiError`, same rationale as the cluckwork
// mock above).
vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return { ...actual, stepUp: vi.fn() };
});

const mockListUsers = vi.mocked(listUsers);
const mockCreateUser = vi.mocked(createUser);
const mockUpdateUser = vi.mocked(updateUser);
const mockSetUserPassword = vi.mocked(setUserPassword);
const mockChangeUserRole = vi.mocked(changeUserRole);
const mockChangeUserEmail = vi.mocked(changeUserEmail);
const mockDisableUser = vi.mocked(disableUser);
const mockEnableUser = vi.mocked(enableUser);
const mockListAssignments = vi.mocked(listFlockAssignments);
const mockAssignFlock = vi.mocked(assignFlock);
const mockUnassignFlock = vi.mocked(unassignFlock);
const mockListFlocks = vi.mocked(listFlocks);
const mockStepUp = vi.mocked(stepUp);

const WORKER_USER: User = {
  id: "u-w", email: "worker@farm.test", displayName: "Wendy", role: "Worker", disabledAt: null,
};
const ADMIN_USER: User = {
  id: "u-a", email: "boss@farm.test", displayName: null, role: "Admin", disabledAt: null,
};
// Role wiring fixture (#182, Task 22): ReadOnly is the one role whose enum
// label is NOT its raw wire value (enums:role.ReadOnly = "Read-only"), so it's
// the fixture that actually distinguishes roleLabel(u.role) from a plain
// {u.role} render.
const READONLY_USER: User = {
  id: "u-r", email: "ro@farm.test", displayName: null, role: "ReadOnly", disabledAt: null,
};
// #356 — a disabled worker, and the ADMIN token's OWN row (id "u1" matches
// DEFAULT_ME/the ADMIN token's sub — see renderWithProviders.tsx), used to
// prove the self-target rows offer neither action.
const DISABLED_USER: User = {
  id: "u-d", email: "disabled@farm.test", displayName: "Dana", role: "Worker",
  disabledAt: "2026-08-01T00:00:00Z",
};
const SELF_USER: User = {
  id: "u1", email: "self@farm.test", displayName: null, role: "Admin", disabledAt: null,
};

const flock = (id: string, name: string, status = "Active"): Flock => ({
  ...NO_RECORD_HISTORY,
  id, farmId: "farm", houseId: "house", name, breed: "ISA Brown",
  placementDate: "2026-01-01", initialCount: 100, currentBirds: 98, status,
});
const FLOCK_A = flock("fl1", "Coop A");
const FLOCK_B = flock("fl2", "Coop B");
const FLOCK_ARCHIVED = flock("fl3", "Old Coop", "Archived");

const ASSIGN_1: FlockAssignment = { id: "as1", flockId: "fl1" };

// renderWithProviders seeds a decoded token so AuthProvider derives a role.
// This screen never reads it (the admin gate is external — see role-gating
// block), but we pass realistic tokens anyway.
const ADMIN = { sub: "u1", role: "Admin" };
const WORKER = { sub: "u1" };

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  // Mount fires Promise.all([listUsers(), listFlocks()]); listFlockAssignments
  // runs on the per-user drill-down. Seed safe defaults for all three.
  mockListUsers.mockResolvedValue([WORKER_USER, ADMIN_USER]);
  mockListFlocks.mockResolvedValue([FLOCK_A, FLOCK_B, FLOCK_ARCHIVED]);
  mockListAssignments.mockResolvedValue([]);
  // #360 — every create/reset/role dialog now spends one fresh step-up grant;
  // the default grant keeps the non-step-up tests from hanging on issuance.
  mockStepUp.mockResolvedValue({ token: "grant-default", expiresAt: "2026-01-01T00:05:00Z" });
});

async function renderReady(token: Record<string, unknown>) {
  renderWithProviders(<UsersPage />, { token });
  await screen.findByText("worker@farm.test");
}

// Two comboboxes coexist once a worker's panel is open (the create-form role
// select + the assign-flock select). Identify the flock one unambiguously by
// scoping to the assignment panel's inline-form row — the one holding the
// "Assign flock" button — rather than matching on shared option text, which
// would latch onto the wrong control if the markup grew another "Coop A".
function flockSelect(): HTMLElement {
  const row = screen.getByRole("button", { name: "Assign flock" }).closest(".inline-form");
  if (!row) throw new Error("assign-flock panel not found");
  return within(row as HTMLElement).getByRole("combobox");
}

describe("UsersPage load", () => {
  it("shows a loading state, then the user list once the load resolves", async () => {
    let resolveUsers!: (u: User[]) => void;
    mockListUsers.mockReturnValue(new Promise<User[]>((r) => { resolveUsers = r; }));
    renderWithProviders(<UsersPage />, { token: ADMIN });

    expect(screen.getByText(/Loading/)).toBeInTheDocument();
    resolveUsers([WORKER_USER, ADMIN_USER]);
    expect(await screen.findByText("worker@farm.test")).toBeInTheDocument();
  });

  // The mount-effect error branch (Promise.all rejects → "…error…") is NOT
  // asserted: in this Vitest 3 + React 19 stack a rejection the component does
  // handle is still surfaced as unhandled through an internal promise the test
  // can't reach (a documented false positive). Event-handler error paths, which
  // this file DOES cover, are unaffected.

  it("renders each user's role, a display-name fallback, and a flocks toggle only for workers", async () => {
    await renderReady(ADMIN);

    const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
    expect(within(workerRow).getByText("Worker")).toBeInTheDocument();
    expect(within(workerRow).getByText("Wendy")).toBeInTheDocument();
    expect(within(workerRow).getByRole("button", { name: "flocks" })).toBeInTheDocument();

    const adminRow = screen.getByRole("row", { name: /boss@farm.test/ });
    expect(within(adminRow).getByText("Admin")).toBeInTheDocument();
    expect(within(adminRow).getByText("—")).toBeInTheDocument(); // null displayName
    // Flock scoping is a worker-only affordance — admins never narrow.
    expect(within(adminRow).queryByRole("button", { name: "flocks" })).not.toBeInTheDocument();
  });

  // #182, Task 22 — the table's Role cell renders roleLabel(u.role), not the
  // raw wire value. Worker/Admin (above) are IDENTITY labels, so they'd pass
  // even against a raw {u.role} render; ReadOnly is the one value the enum
  // catalog renders differently ("Read-only") from its wire form ("ReadOnly"),
  // so only this fixture actually proves the helper is wired in.
  it("renders a ReadOnly user's role cell via roleLabel as 'Read-only', not the raw 'ReadOnly' wire value", async () => {
    mockListUsers.mockResolvedValue([WORKER_USER, ADMIN_USER, READONLY_USER]);
    await renderReady(ADMIN);

    const roRow = screen.getByRole("row", { name: /ro@farm.test/ });
    expect(within(roRow).getByText("Read-only")).toBeInTheDocument();
    expect(within(roRow).queryByText("ReadOnly")).not.toBeInTheDocument();
  });
});

// F131: create moved into a dialog — open it, then assert the same behaviour.
const openCreate = () => fireEvent.click(screen.getByRole("button", { name: "New user" }));
const dialog = () => screen.getByRole("dialog");

describe("UsersPage create", () => {
  it("creates a user with the entered email + role (off default) and a present password + idempotency key", async () => {
    mockCreateUser.mockResolvedValue({ id: "u-new" });
    await renderReady(ADMIN);
    openCreate();

    // Runtime-generated credential — no literal secret in source (GitGuardian).
    const password = `pw-${crypto.randomUUID()}`;
    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "  New@Farm.test  " } });
    // #360 — two password fields now coexist (the new user's and the
    // caller's current one); the first is the new account's.
    fireEvent.change(within(dialog()).getAllByLabelText(/Password/)[0], { target: { value: password } });
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Manager" } }); // off the "Worker" default
    // #360 — every creation re-confirms the caller's current password, even
    // for a Manager.
    expect(within(dialog()).getByLabelText(/Your current password/)).toBeRequired();
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" }));
    });

    const body = mockCreateUser.mock.calls[0][0];
    expect(body).toMatchObject({ email: "New@Farm.test", role: "Manager" }); // email trimmed, role chosen
    // Pin that the exact typed password reaches the request body (not a shape check).
    expect(body.password).toBe(password);
    expect(mockCreateUser.mock.calls[0][1]).toEqual(expect.any(String)); // idempotency key
    // #360 — the typed proof goes to stepUp(), and the returned grant rides the
    // write once.
    expect(mockStepUp).toHaveBeenCalledWith(OWNER_STEP_UP_PASSWORD);
    expect(mockCreateUser).toHaveBeenCalledWith(
      expect.objectContaining({ role: "Manager" }), expect.any(String), "grant-default");

    // Success surfaces a confirmation on the page, dismisses the dialog, and
    // resets the form behind it.
    expect(await screen.findByText(/Manager account created for New@Farm\.test/)).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    openCreate();
    expect(within(dialog()).getByLabelText("Email *")).toHaveValue("");
  });

  it("sends the trimmed Name when one is entered, and omits it when left blank (#163)", async () => {
    mockCreateUser.mockResolvedValue({ id: "u-new" });
    await renderReady(ADMIN);

    // With a name.
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "named@farm.test" } });
    fireEvent.change(within(dialog()).getAllByLabelText(/Password/)[0], { target: { value: `pw-${crypto.randomUUID()}` } });
    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "  Ada Lovelace  " } });
    // #360 — the proof field is present for every creation; a grant is spent
    // on the write.
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" })); });
    expect(mockCreateUser.mock.calls[0][0]).toMatchObject({ email: "named@farm.test", name: "Ada Lovelace" });
    expect(mockCreateUser.mock.calls[0][2]).toBe("grant-default");

    // Without a name → the field is omitted (undefined), not sent blank. #360
    // — a fresh grant per attempt: the previous one was spent (and the typed
    // proof cleared before awaiting), so retype it.
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "anon@farm.test" } });
    fireEvent.change(within(dialog()).getAllByLabelText(/Password/)[0], { target: { value: `pw-${crypto.randomUUID()}` } });
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" })); });
    expect(mockCreateUser.mock.calls[1][0].name).toBeUndefined();
    expect(mockCreateUser.mock.calls[1][2]).toBe("grant-default");
  });

  it("replays the SAME create key after a failure, and rotates it after success", async () => {
    mockCreateUser.mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    mockCreateUser.mockResolvedValue({ id: "u-new" });
    await renderReady(ADMIN);

    openCreate();
    const emailInput = () => within(dialog()).getByLabelText("Email *");
    // #360 — the /Password/ label matches two inputs (the new user's and the
    // caller's current one); the new user's is the first.
    const pwInput = () => within(dialog()).getAllByLabelText(/Password/)[0];
    const proofInput = () => within(dialog()).getByLabelText(/Your current password/);
    const submit = () => within(dialog()).getByRole("button", { name: "Create user" });

    // Attempt 1 — same email → same scope; fails, so the key is kept. #360 —
    // the proof field is required on every attempt; production clears the
    // typed proof before awaiting, so it must be retyped per attempt.
    fireEvent.change(emailInput(), { target: { value: "one@farm.test" } });
    fireEvent.change(pwInput(), { target: { value: `pw-${crypto.randomUUID()}` } });
    fireEvent.change(proofInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => { fireEvent.click(submit()); });
    // A failure keeps the dialog up with the error inside it.
    expect(within(dialog()).getByText(/Server error|boom/)).toBeInTheDocument();

    // Attempt 2 — email/password survive a failure; resubmit as-is → replay.
    // The step-up proof does NOT survive: retype it (a new grant is spent).
    fireEvent.change(proofInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => { fireEvent.click(submit()); });

    // Attempt 3 — success closed the dialog and reset the form, so reopen and
    // refill the same email → fresh key. Proof retyped again.
    openCreate();
    fireEvent.change(emailInput(), { target: { value: "one@farm.test" } });
    fireEvent.change(pwInput(), { target: { value: `pw-${crypto.randomUUID()}` } });
    fireEvent.change(proofInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => { fireEvent.click(submit()); });

    const k1 = mockCreateUser.mock.calls[0][1];
    const k2 = mockCreateUser.mock.calls[1][1];
    const k3 = mockCreateUser.mock.calls[2][1];
    expect(k2).toBe(k1); // failure kept the key → exact replay
    expect(k3).not.toBe(k2); // success rotated it → next write is fresh
    // #360 — a grant was minted for every attempt and attached to each write.
    expect(mockStepUp).toHaveBeenCalledTimes(3);
    expect(k1).toBeDefined();
    expect(mockCreateUser.mock.calls[0][2]).toBe("grant-default");
    expect(mockCreateUser.mock.calls[1][2]).toBe("grant-default");
    expect(mockCreateUser.mock.calls[2][2]).toBe("grant-default");
  });
});

describe("UsersPage edit name (#163)", () => {
  const editRow = (rowName: RegExp) =>
    fireEvent.click(within(screen.getByRole("row", { name: rowName })).getByRole("button", { name: "edit" }));

  it("edits a user's name via the row 'edit' action, sending id + name + a key, then refreshes", async () => {
    mockUpdateUser.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    // The admin row starts nameless (—); open its edit dialog seeded with "".
    editRow(/boss@farm.test/);
    const box = within(dialog()).getByLabelText("Name");
    expect(box).toHaveValue("");
    fireEvent.change(box, { target: { value: "  Grace Hopper  " } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Save" })); });

    expect(mockUpdateUser).toHaveBeenCalledWith("u-a", { name: "Grace Hopper" }, expect.any(String));
    expect(mockListUsers).toHaveBeenCalledTimes(2); // initial load + post-update refresh
    expect(await screen.findByText(/Updated boss@farm\.test/)).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("clears a name by saving the edit dialog blank (sends name: null)", async () => {
    mockUpdateUser.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    // The worker row is prefilled with its current name; blank it out.
    editRow(/worker@farm.test/);
    expect(within(dialog()).getByLabelText("Name")).toHaveValue("Wendy"); // seeded from displayName
    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "   " } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Save" })); });

    expect(mockUpdateUser).toHaveBeenCalledWith("u-w", { name: null }, expect.any(String));
  });

  it("rotates the update key once the write is confirmed, so a changed retry after a failed refresh isn't replayed (#163)", async () => {
    mockUpdateUser.mockResolvedValue(undefined);
    // Mount load ok; the refresh AFTER the first save fails; later loads ok.
    mockListUsers
      .mockResolvedValueOnce([WORKER_USER, ADMIN_USER])
      .mockRejectedValueOnce(new ApiError(500, "Server error", "boom"))
      .mockResolvedValue([WORKER_USER, ADMIN_USER]);
    await renderReady(ADMIN);

    editRow(/boss@farm.test/);
    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "Alice" } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Save" })); });
    // The write succeeded but the refresh failed → dialog stays open with the error.
    expect(within(dialog()).getByText(/Server error|boom/)).toBeInTheDocument();

    // Change the value and save again — a DIFFERENT key (the confirmed write cleared it),
    // so the server can't replay the cached "Alice" response for the "Bob" edit.
    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "Bob" } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Save" })); });

    expect(mockUpdateUser.mock.calls[1][2]).not.toBe(mockUpdateUser.mock.calls[0][2]); // key rotated
    expect(mockUpdateUser.mock.calls[1][1]).toEqual({ name: "Bob" });
  });

  it("keeps the edit dialog open and shows the error when the update fails", async () => {
    mockUpdateUser.mockRejectedValue(new ApiError(500, "Server error", "boom"));
    await renderReady(ADMIN);

    editRow(/worker@farm.test/);
    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "New Name" } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Save" })); });

    expect(within(dialog()).getByText(/Server error|boom/)).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });
});

describe("UsersPage set password (#165)", () => {
  const openPw = (rowName: RegExp) =>
    fireEvent.click(within(screen.getByRole("row", { name: rowName })).getByRole("button", { name: "password" }));

  // Runtime-generated so no literal secret lands in source (GitGuardian).
  const freshPassword = () => `Aa1!${crypto.randomUUID()}`;

  it("sets a user's password from the row action, sending id + password + a key", async () => {
    mockSetUserPassword.mockResolvedValue(undefined);
    await renderReady(ADMIN);
    const password = freshPassword();

    openPw(/worker@farm.test/);
    fireEvent.change(within(dialog()).getByLabelText(/New password/), { target: { value: password } });
    fireEvent.change(within(dialog()).getByLabelText(/Confirm new password/), { target: { value: password } });
    // #360 — the proof field is present for every reset, including a Worker's,
    // and the returned grant rides the write.
    expect(within(dialog()).getByLabelText(/Your current password/)).toBeRequired();
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Set password" })); });

    expect(mockStepUp).toHaveBeenCalledWith(OWNER_STEP_UP_PASSWORD);
    expect(mockSetUserPassword).toHaveBeenCalledWith(
      "u-w", { newPassword: password }, expect.any(String), "grant-default");
    // Success closes the dialog and says the target was signed out.
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(await screen.findByText(/signed out everywhere/i)).toBeInTheDocument();
  });

  it("refuses a mismatched confirmation without calling the server", async () => {
    await renderReady(ADMIN);

    openPw(/worker@farm.test/);
    fireEvent.change(within(dialog()).getByLabelText(/New password/), { target: { value: freshPassword() } });
    fireEvent.change(within(dialog()).getByLabelText(/Confirm new password/), { target: { value: freshPassword() } });
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Set password" })); });

    // The mismatch is rejected before any proof is minted or spent.
    expect(mockSetUserPassword).not.toHaveBeenCalled();
    expect(mockStepUp).not.toHaveBeenCalled();
    expect(within(dialog()).getByText(/don't match/i)).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument(); // stays open to fix it
  });

  it("keeps the dialog open and shows the error when the server rejects it", async () => {
    mockSetUserPassword.mockRejectedValue(new ApiError(400, "Bad request", "too weak"));
    await renderReady(ADMIN);
    const password = freshPassword();

    openPw(/worker@farm.test/);
    fireEvent.change(within(dialog()).getByLabelText(/New password/), { target: { value: password } });
    fireEvent.change(within(dialog()).getByLabelText(/Confirm new password/), { target: { value: password } });
    // #360 — proof is retyped per attempt (it was cleared before the first
    // await, and the first grant was spent even though the write was rejected).
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Set password" })); });

    expect(within(dialog()).getByText(/too weak|Bad request/)).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });
});

describe("UsersPage change role (#355)", () => {
  const openRole = (rowName: RegExp) =>
    fireEvent.click(within(screen.getByRole("row", { name: rowName })).getByRole("button", { name: "role" }));

  it("opens the dialog seeded with the target's current role", async () => {
    await renderReady(ADMIN);
    openRole(/worker@farm.test/);
    expect(within(dialog()).getByLabelText("Role")).toHaveValue("Worker");
  });

  it("changes a user's role from the row 'role' action, sending id + role + a key", async () => {
    mockChangeUserRole.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    openRole(/worker@farm.test/);
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Manager" } });
    // #360 — every role change, including a Manager promotion, re-confirms the
    // caller's current password and spends the returned grant.
    expect(within(dialog()).getByLabelText(/Your current password/)).toBeRequired();
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Change role" }));
    });

    expect(mockStepUp).toHaveBeenCalledWith(OWNER_STEP_UP_PASSWORD);
    expect(mockChangeUserRole).toHaveBeenCalledWith(
      "u-w", { role: "Manager" }, expect.any(String), "grant-default");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(await screen.findByText(/worker@farm\.test is now Manager/)).toBeInTheDocument();
    expect(mockListUsers).toHaveBeenCalledTimes(2); // initial load + post-change refresh
  });

  it("keeps the dialog open and shows the error when the server rejects it", async () => {
    mockChangeUserRole.mockRejectedValue(new ApiError(422, "Users.LastOwner", "cannot demote the sole remaining owner"));
    await renderReady(ADMIN);

    openRole(/boss@farm.test/);
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Manager" } });
    // #360 — proof is retyped per attempt.
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Change role" }));
    });

    expect(within(dialog()).getByText(/sole remaining owner/)).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("closes the dialog on Cancel without writing, and clears any typed step-up password on reopen", async () => {
    await renderReady(ADMIN);
    openRole(/boss@farm.test/);
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Admin" } });
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), { target: { value: "typed" } });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockChangeUserRole).not.toHaveBeenCalled();

    openRole(/boss@farm.test/);
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Admin" } });
    expect(within(dialog()).getByLabelText(/Your current password/)).toHaveValue("");
  });
});

describe("UsersPage dismissed step-up continuations (#360)", () => {
  function deferred<T>() {
    let resolve!: (value: T) => void;
    const promise = new Promise<T>((ok) => { resolve = ok; });
    return { promise, resolve };
  }

  it("does not let a dismissed create continuation write or clear a reopened form", async () => {
    const grant = deferred<{ token: string; expiresAt: string }>();
    mockStepUp.mockReturnValue(grant.promise);
    mockCreateUser.mockResolvedValue({ id: "u-new" });
    await renderReady(ADMIN);

    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Email *"), {
      target: { value: "dismissed@farm.test" },
    });
    fireEvent.change(within(dialog()).getAllByLabelText(/Password/)[0], {
      target: { value: `pw-${crypto.randomUUID()}` },
    });
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" }));
    });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Email *"), {
      target: { value: "current@farm.test" },
    });
    fireEvent.change(within(dialog()).getAllByLabelText(/Password/)[0], {
      target: { value: "current form value" },
    });

    await act(async () => {
      grant.resolve({ token: "late-grant", expiresAt: "2026-01-01T00:05:00Z" });
    });

    expect(mockCreateUser).not.toHaveBeenCalled();
    expect(within(dialog()).getByLabelText("Email *")).toHaveValue("current@farm.test");
    expect(within(dialog()).getAllByLabelText(/Password/)[0]).toHaveValue("current form value");
  });

  it("does not let a dismissed password-reset continuation write", async () => {
    const grant = deferred<{ token: string; expiresAt: string }>();
    mockStepUp.mockReturnValue(grant.promise);
    mockSetUserPassword.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    fireEvent.click(within(screen.getByRole("row", { name: /worker@farm.test/ }))
      .getByRole("button", { name: "password" }));
    const newPassword = `Aa1!${crypto.randomUUID()}`;
    fireEvent.change(within(dialog()).getByLabelText(/New password/), {
      target: { value: newPassword },
    });
    fireEvent.change(within(dialog()).getByLabelText(/Confirm new password/), {
      target: { value: newPassword },
    });
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Set password" }));
    });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    await act(async () => {
      grant.resolve({ token: "late-grant", expiresAt: "2026-01-01T00:05:00Z" });
    });

    expect(mockSetUserPassword).not.toHaveBeenCalled();
  });

  it("does not let a dismissed role-change continuation write", async () => {
    const grant = deferred<{ token: string; expiresAt: string }>();
    mockStepUp.mockReturnValue(grant.promise);
    mockChangeUserRole.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    fireEvent.click(within(screen.getByRole("row", { name: /worker@farm.test/ }))
      .getByRole("button", { name: "role" }));
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Manager" } });
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Change role" }));
    });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    await act(async () => {
      grant.resolve({ token: "late-grant", expiresAt: "2026-01-01T00:05:00Z" });
    });

    expect(mockChangeUserRole).not.toHaveBeenCalled();
  });
});

describe("UsersPage change email (#357)", () => {
  const openEmail = (rowName: RegExp) =>
    fireEvent.click(within(screen.getByRole("row", { name: rowName }))
      .getByRole("button", { name: /change email/i }));
  const emailInput = () => within(dialog()).getByLabelText("Login email");
  const passwordInput = () => within(dialog()).getByLabelText(/Your current password/);
  const submit = () => within(dialog()).getByRole("button", { name: "Change email" });

  function deferred<T>() {
    let resolve!: (value: T) => void;
    let reject!: (reason?: unknown) => void;
    const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail; });
    return { promise, resolve, reject };
  }

  it("opens a dedicated Change email dialog for the selected row", async () => {
    await renderReady(ADMIN);

    openEmail(/worker@farm.test/);

    expect(screen.getByRole("dialog", { name: /Change email — worker@farm\.test/ })).toBeInTheDocument();
    expect(emailInput()).toHaveValue("worker@farm.test");
    expect(passwordInput()).toHaveAttribute("autocomplete", "current-password");
  });

  it("requires step-up, clears the password, and sends trimmed email with one idempotency key", async () => {
    const grant = deferred<{ token: string; expiresAt: string }>();
    mockStepUp.mockReturnValue(grant.promise);
    mockChangeUserEmail.mockResolvedValue(undefined);
    await renderReady(ADMIN);
    openEmail(/worker@farm.test/);
    fireEvent.change(emailInput(), { target: { value: "  corrected@farm.test  " } });
    fireEvent.change(passwordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });

    await act(async () => { fireEvent.click(submit()); });
    expect(mockStepUp).toHaveBeenCalledWith(OWNER_STEP_UP_PASSWORD);
    expect(passwordInput()).toHaveValue("");

    await act(async () => grant.resolve({ token: "email-grant", expiresAt: "2026-01-01T00:05:00Z" }));
    expect(mockChangeUserEmail).toHaveBeenCalledWith(
      "u-w", { email: "corrected@farm.test" }, expect.any(String), "email-grant");
    expect(mockChangeUserEmail).toHaveBeenCalledTimes(1);
  });

  it("renders Users.DuplicateEmail beside the email input with aria-invalid", async () => {
    mockStepUp.mockResolvedValue({ token: "email-grant", expiresAt: "2026-01-01T00:05:00Z" });
    mockChangeUserEmail.mockRejectedValue(
      new ApiError(409, "Users.DuplicateEmail", "A user with this email already exists."));
    await renderReady(ADMIN);
    openEmail(/worker@farm.test/);
    fireEvent.change(emailInput(), { target: { value: "taken@farm.test" } });
    fireEvent.change(passwordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });

    await act(async () => { fireEvent.click(submit()); });

    const fieldError = within(dialog()).getByText("A user with this email already exists.");
    expect(fieldError).toHaveAttribute("role", "alert");
    expect(emailInput()).toHaveAttribute("aria-invalid", "true");
    expect(emailInput()).toHaveAttribute("aria-describedby", fieldError.id);
    expect(within(dialog()).getAllByRole("alert")).toEqual([fieldError]);
  });

  it("clears the field error on edit, close, and target change", async () => {
    mockStepUp.mockResolvedValue({ token: "email-grant", expiresAt: "2026-01-01T00:05:00Z" });
    mockChangeUserEmail.mockRejectedValue(
      new ApiError(409, "Users.DuplicateEmail", "A user with this email already exists."));
    await renderReady(ADMIN);
    openEmail(/worker@farm.test/);
    fireEvent.change(emailInput(), { target: { value: "taken@farm.test" } });
    fireEvent.change(passwordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => { fireEvent.click(submit()); });
    expect(emailInput()).toHaveAttribute("aria-invalid", "true");

    fireEvent.change(emailInput(), { target: { value: "fixed@farm.test" } });
    expect(emailInput()).toHaveAttribute("aria-invalid", "false");
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    openEmail(/worker@farm.test/);
    expect(emailInput()).toHaveAttribute("aria-invalid", "false");
    openEmail(/boss@farm.test/);
    expect(emailInput()).toHaveValue("boss@farm.test");
    expect(emailInput()).toHaveAttribute("aria-invalid", "false");
  });

  it.each([
    ["same", /worker@farm\.test/, "worker@farm.test"],
    ["different", /boss@farm\.test/, "boss@farm.test"],
  ])("cancel/reopen for the %s target invalidates a step-up continuation that later succeeds",
    async (_, reopenedRow, reopenedEmail) => {
      const late = deferred<{ token: string; expiresAt: string }>();
      mockStepUp.mockReturnValue(late.promise);
      mockChangeUserEmail.mockResolvedValue(undefined);
      await renderReady(ADMIN);
      openEmail(/worker@farm.test/);
      fireEvent.change(emailInput(), { target: { value: "abandoned@farm.test" } });
      fireEvent.change(passwordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
      await act(async () => { fireEvent.click(submit()); });
      fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
      openEmail(reopenedRow);

      await act(async () => late.resolve({ token: "abandoned-grant", expiresAt: "2026-01-01T00:05:00Z" }));

      expect(mockChangeUserEmail).not.toHaveBeenCalled();
      expect(emailInput()).toHaveValue(reopenedEmail);
    });

  it.each([
    ["same", /worker@farm\.test/, "worker@farm.test"],
    ["different", /boss@farm\.test/, "boss@farm.test"],
  ])("cancel/reopen for the %s target ignores a step-up continuation that later fails",
    async (_, reopenedRow, reopenedEmail) => {
      const late = deferred<{ token: string; expiresAt: string }>();
      mockStepUp.mockReturnValue(late.promise);
      await renderReady(ADMIN);
      openEmail(/worker@farm.test/);
      fireEvent.change(emailInput(), { target: { value: "abandoned@farm.test" } });
      fireEvent.change(passwordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
      await act(async () => { fireEvent.click(submit()); });
      fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
      openEmail(reopenedRow);

      await act(async () => late.reject(
        new ApiError(403, "StepUp.Invalid", "The abandoned password proof failed.")));

      expect(emailInput()).toHaveValue(reopenedEmail);
      expect(within(dialog()).queryByRole("alert")).not.toBeInTheDocument();
    });

  it.each([
    ["same", /worker@farm\.test/, "worker@farm.test"],
    ["different", /boss@farm\.test/, "boss@farm.test"],
  ])("after a PUT is sent, cancel/reopen for the %s target ignores its late success in the reopened dialog",
    async (_, reopenedRow, reopenedEmail) => {
      const late = deferred<void>();
      mockStepUp.mockResolvedValue({ token: "email-grant", expiresAt: "2026-01-01T00:05:00Z" });
      mockChangeUserEmail.mockReturnValue(late.promise);
      await renderReady(ADMIN);
      openEmail(/worker@farm.test/);
      fireEvent.change(emailInput(), { target: { value: "abandoned@farm.test" } });
      fireEvent.change(passwordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
      await act(async () => { fireEvent.click(submit()); });
      // Cancel abandons this dialog instance's UI ownership; it cannot abort
      // the PUT that changeUserEmail has already sent to the server.
      fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
      openEmail(reopenedRow);

      await act(async () => late.resolve(undefined));

      expect(screen.getByRole("dialog")).toBeInTheDocument();
      expect(emailInput()).toHaveValue(reopenedEmail);
      expect(screen.queryByText(/Login email changed to abandoned@farm\.test/)).not.toBeInTheDocument();
    });

  it.each([
    ["same", /worker@farm\.test/, "worker@farm.test"],
    ["different", /boss@farm\.test/, "boss@farm.test"],
  ])("after a PUT is sent, cancel/reopen for the %s target ignores its late failure in the reopened dialog",
    async (_, reopenedRow, reopenedEmail) => {
    const late = deferred<void>();
    mockStepUp.mockResolvedValue({ token: "email-grant", expiresAt: "2026-01-01T00:05:00Z" });
    mockChangeUserEmail.mockReturnValue(late.promise);
    await renderReady(ADMIN);
    openEmail(/worker@farm.test/);
    fireEvent.change(emailInput(), { target: { value: "taken@farm.test" } });
    fireEvent.change(passwordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => { fireEvent.click(submit()); });
    // Cancel abandons this dialog instance's UI ownership; it cannot abort
    // the PUT that changeUserEmail has already sent to the server.
    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    openEmail(reopenedRow);

    await act(async () => late.reject(
      new ApiError(409, "Users.DuplicateEmail", "A user with this email already exists.")));

    expect(emailInput()).toHaveValue(reopenedEmail);
    expect(emailInput()).toHaveAttribute("aria-invalid", "false");
    expect(within(dialog()).queryByText("A user with this email already exists.")).not.toBeInTheDocument();
  });

  it("freezes the submitted email while a late duplicate response is pending for the same target", async () => {
    const user = userEvent.setup();
    const late = deferred<void>();
    mockStepUp.mockResolvedValue({ token: "email-grant", expiresAt: "2026-01-01T00:05:00Z" });
    mockChangeUserEmail.mockReturnValue(late.promise);
    await renderReady(ADMIN);
    openEmail(/worker@farm.test/);
    fireEvent.change(emailInput(), { target: { value: "taken@farm.test" } });
    fireEvent.change(passwordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });

    await act(async () => { fireEvent.click(submit()); });

    expect(emailInput()).toBeDisabled();
    expect(passwordInput()).toBeDisabled();
    await user.type(emailInput(), "different@farm.test");
    expect(emailInput()).toHaveValue("taken@farm.test");

    await act(async () => late.reject(
      new ApiError(409, "Users.DuplicateEmail", "A user with this email already exists.")));

    expect(emailInput()).toHaveValue("taken@farm.test");
    expect(emailInput()).toHaveAttribute("aria-invalid", "true");
  });

  it("does not render a generic dialog failure when a self-change refresh reports credentials superseded", async () => {
    mockListUsers.mockReset()
      .mockResolvedValueOnce([WORKER_USER, ADMIN_USER, SELF_USER])
      .mockRejectedValueOnce(new ApiError(
        401, "Auth.CredentialsSuperseded", "Your credentials changed."));
    mockStepUp.mockResolvedValue({ token: "email-grant", expiresAt: "2026-01-01T00:05:00Z" });
    mockChangeUserEmail.mockResolvedValue(undefined);
    await renderReady(ADMIN);
    openEmail(/self@farm.test/);
    fireEvent.change(emailInput(), { target: { value: "new-self@farm.test" } });
    fireEvent.change(passwordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });

    await act(async () => { fireEvent.click(submit()); });

    expect(mockListUsers).toHaveBeenCalledTimes(2);
    expect(within(dialog()).queryByText("Your credentials changed.")).not.toBeInTheDocument();
  });

  it("self-change lets the next authenticated request enter the credentials-superseded sign-in path", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({
        title: "Auth.CredentialsSuperseded",
        detail: "Your credentials changed.",
      }), { status: 401, headers: { "Content-Type": "application/problem+json" } }))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        title: "Identity.InvalidRefreshToken",
        detail: "The refresh token is invalid.",
      }), { status: 401, headers: { "Content-Type": "application/problem+json" } }));
    vi.stubGlobal("fetch", fetchMock);
    mockListUsers.mockReset()
      .mockResolvedValueOnce([WORKER_USER, ADMIN_USER, SELF_USER])
      .mockImplementationOnce(() => apiGet<User[]>("/users"));
    mockStepUp.mockResolvedValue({ token: "email-grant", expiresAt: "2026-01-01T00:05:00Z" });
    mockChangeUserEmail.mockResolvedValue(undefined);
    renderWithProviders(
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route element={<ProtectedRoute />}>
          <Route path="/users" element={<UsersPage />} />
        </Route>
      </Routes>,
      {
        route: "/users",
        token: { sub: "u1", role: "Admin", account_id: "account-1" },
        me: {
          id: SELF_USER.id, email: SELF_USER.email, name: null, role: SELF_USER.role,
          language: null, preferredStepperUnit: null,
        },
      },
    );
    await screen.findByText("worker@farm.test");
    openEmail(/self@farm.test/);
    fireEvent.change(emailInput(), { target: { value: "new-self@farm.test" } });
    fireEvent.change(passwordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });

    await act(async () => { fireEvent.click(submit()); });

    expect(await screen.findByText(i18n.t("auth:credentialsSuperseded"))).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockListUsers).toHaveBeenCalledTimes(2);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(getAccessToken()).toBeNull();
  });

  it("a successful non-self change refreshes the row and reports the new email", async () => {
    mockStepUp.mockResolvedValue({ token: "email-grant", expiresAt: "2026-01-01T00:05:00Z" });
    mockChangeUserEmail.mockResolvedValue(undefined);
    mockListUsers
      .mockResolvedValueOnce([WORKER_USER, ADMIN_USER])
      .mockResolvedValueOnce([{ ...WORKER_USER, email: "corrected@farm.test" }, ADMIN_USER]);
    await renderReady(ADMIN);
    openEmail(/worker@farm.test/);
    fireEvent.change(emailInput(), { target: { value: "corrected@farm.test" } });
    fireEvent.change(passwordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });

    await act(async () => { fireEvent.click(submit()); });

    expect(await screen.findByText("corrected@farm.test")).toBeInTheDocument();
    expect(screen.getByText(/Login email changed to corrected@farm\.test/)).toBeInTheDocument();
    expect(mockListUsers).toHaveBeenCalledTimes(2);
  });

  it("does not claim that a confirmation email will be sent", async () => {
    await renderReady(ADMIN);
    openEmail(/worker@farm.test/);

    expect(within(dialog()).getByText(/no confirmation email is sent/i)).toBeInTheDocument();
    expect(within(dialog()).queryByText(/confirmation email will be sent/i)).not.toBeInTheDocument();
  });
});

describe("UsersPage change-role step-up (#308, #355)", () => {
  const openRole = (rowName: RegExp) =>
    fireEvent.click(within(screen.getByRole("row", { name: rowName })).getByRole("button", { name: "role" }));
  const ownerPasswordInput = () => within(dialog()).getByLabelText(/Your current password/);
  const selectAdminRole = () =>
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Admin" } });

  // #360 — the field is unconditional: present on every role dialog, whatever
  // the current or requested role.
  it("always shows the required step-up field, whatever the requested role", async () => {
    await renderReady(ADMIN);
    openRole(/worker@farm.test/);

    expect(within(dialog()).getByLabelText(/Your current password/)).toBeRequired();
    selectAdminRole();
    expect(ownerPasswordInput()).toBeRequired();
  });

  it("demoting an existing Owner still re-confirms the password and spends a grant", async () => {
    mockChangeUserRole.mockResolvedValue(undefined);
    await renderReady(ADMIN);
    openRole(/boss@farm.test/);
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Manager" } });
    expect(ownerPasswordInput()).toBeRequired();
    fireEvent.change(ownerPasswordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Change role" }));
    });

    expect(mockStepUp).toHaveBeenCalledWith(OWNER_STEP_UP_PASSWORD);
    expect(mockChangeUserRole).toHaveBeenCalledWith(
      "u-a", { role: "Manager" }, expect.any(String), "grant-default");
  });

  it("promoting to Owner exchanges the current password for a grant and attaches it", async () => {
    mockStepUp.mockResolvedValue({ token: "grant-789", expiresAt: "2026-01-01T00:05:00Z" });
    mockChangeUserRole.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    openRole(/worker@farm.test/);
    selectAdminRole();
    fireEvent.change(ownerPasswordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Change role" }));
    });

    expect(mockStepUp).toHaveBeenCalledWith(OWNER_STEP_UP_PASSWORD);
    expect(mockChangeUserRole).toHaveBeenCalledWith(
      "u-w", { role: "Admin" }, expect.any(String), "grant-789");
  });

  it("never stores the entered step-up password: reopening after a successful use shows it empty", async () => {
    mockStepUp.mockResolvedValue({ token: "grant-789", expiresAt: "2026-01-01T00:05:00Z" });
    mockChangeUserRole.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    openRole(/worker@farm.test/);
    selectAdminRole();
    fireEvent.change(ownerPasswordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Change role" }));
    });

    openRole(/worker@farm.test/);
    selectAdminRole();
    expect(ownerPasswordInput()).toHaveValue("");
  });

  // Same controlled-AuthContext technique the create/password dialogs use
  // above, applied to the role dialog's own step-up field — the dialog
  // stays mounted across the rerender (its local state untouched), so an
  // empty value here proves the logout effect cleared it, not incidental
  // unmounting.
  it("clears any half-entered step-up password the instant the session ends", async () => {
    const me: Me = {
      id: "u1", email: "test@farm.local", name: null, role: "Admin", language: null,
      preferredStepperUnit: null,
    };
    const tree = (isAuthenticated: boolean) => (
      <MemoryRouter initialEntries={["/"]}>
        <AuthContext.Provider value={{
          isAuthenticated, isLoading: false, isAdmin: true, role: "Admin" as Role, userId: "u1",
          mustChangePassword: false,
          unauthenticatedReason: null,
          login: vi.fn(), logout: vi.fn(),
        }}
        >
          <MeContext.Provider value={me}>
            <FarmContext.Provider value={farmState({ farm: null })}>
              <UsersPage />
            </FarmContext.Provider>
          </MeContext.Provider>
        </AuthContext.Provider>
      </MemoryRouter>
    );
    const view = render(tree(true));
    await screen.findByText("worker@farm.test");

    openRole(/worker@farm.test/);
    selectAdminRole();
    fireEvent.change(ownerPasswordInput(), { target: { value: "still-typing" } });
    expect(ownerPasswordInput()).toHaveValue("still-typing");

    view.rerender(tree(false)); // simulated logout

    expect(ownerPasswordInput()).toHaveValue("");
  });
});

// #356 — disable/enable a user, both in ONE dialog that is itself the
// confirmation: a destructive warning, an OPTIONAL reason (disable only —
// the API's DisableUserCommand.Reason is nullable), and the mandatory
// step-up proof, matching the other durable user-access mutations.
describe("UsersPage disable/enable (#356)", () => {
  const disableRow = (rowName: RegExp) =>
    within(screen.getByRole("row", { name: rowName })).getByRole("button", { name: "disable" });
  const enableRow = (rowName: RegExp) =>
    within(screen.getByRole("row", { name: rowName })).getByRole("button", { name: "enable" });

  // Same idiom as the #236 pending-states block below (client.test.ts style):
  // a promise this test controls, so it can assert what the component does
  // WHILE a request is genuinely in flight, not just before/after it.
  function deferred<T>() {
    let resolve!: (v: T) => void;
    const promise = new Promise<T>((r) => { resolve = r; });
    return { promise, resolve };
  }

  it("renders a disabled user's row muted with a Disabled badge, offering Enable and not Disable", async () => {
    mockListUsers.mockResolvedValue([WORKER_USER, DISABLED_USER]);
    await renderReady(ADMIN);

    const row = screen.getByRole("row", { name: /disabled@farm.test/ });
    expect(row).toHaveClass("muted");
    expect(within(row).getByText("Disabled")).toBeInTheDocument();
    expect(within(row).getByRole("button", { name: "enable" })).toBeInTheDocument();
    expect(within(row).queryByRole("button", { name: "disable" })).not.toBeInTheDocument();

    // The still-active sibling row stays unmuted, un-badged, and offers Disable.
    const activeRow = screen.getByRole("row", { name: /worker@farm.test/ });
    expect(activeRow).not.toHaveClass("muted");
    expect(within(activeRow).queryByText("Disabled")).not.toBeInTheDocument();
    expect(within(activeRow).getByRole("button", { name: "disable" })).toBeInTheDocument();
  });

  it("offers neither Disable nor Enable on the caller's own row", async () => {
    mockListUsers.mockResolvedValue([WORKER_USER, SELF_USER]);
    await renderReady(ADMIN); // ADMIN token's sub is "u1", matching SELF_USER's id

    const selfRow = screen.getByRole("row", { name: /self@farm.test/ });
    expect(within(selfRow).queryByRole("button", { name: "disable" })).not.toBeInTheDocument();
    expect(within(selfRow).queryByRole("button", { name: "enable" })).not.toBeInTheDocument();
    // A non-self row in the same render is unaffected.
    expect(within(screen.getByRole("row", { name: /worker@farm.test/ }))
      .getByRole("button", { name: "disable" })).toBeInTheDocument();
  });

  // Round-10 codex review of #492: SessionProvider deliberately keeps the
  // shell visible with me === null when /me fails, so the self-target guard
  // must not depend on /me — a submit that reaches the server for a
  // self-target only 400s, after already spending a step-up password
  // confirmation. `me: null` here reproduces exactly that failure.
  it("hides self-target actions from the TOKEN's id even when /me is null", async () => {
    mockListUsers.mockResolvedValue([WORKER_USER, SELF_USER]);
    renderWithProviders(<UsersPage />, { token: ADMIN, me: null }); // ADMIN token's sub is "u1", matching SELF_USER's id
    await screen.findByText("worker@farm.test");

    const selfRow = screen.getByRole("row", { name: /self@farm.test/ });
    expect(within(selfRow).queryByRole("button", { name: "disable" })).not.toBeInTheDocument();
    expect(within(selfRow).queryByRole("button", { name: "enable" })).not.toBeInTheDocument();
    expect(within(screen.getByRole("row", { name: /worker@farm.test/ }))
      .getByRole("button", { name: "disable" })).toBeInTheDocument();
  });

  it("wires the destructive warning into the dialog's aria-describedby", async () => {
    mockListUsers.mockResolvedValue([WORKER_USER, ADMIN_USER, DISABLED_USER]);
    await renderReady(ADMIN);
    fireEvent.click(disableRow(/worker@farm.test/));

    const dialog = await screen.findByRole("dialog", { name: /Disable — worker@farm\.test/ });
    const describedBy = dialog.getAttribute("aria-describedby");
    expect(describedBy).toBeTruthy();
    expect(document.getElementById(describedBy!)).toHaveTextContent(/signed out of every device/);

    // Enable has no destructive-warning paragraph to point at.
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    fireEvent.click(enableRow(/disabled@farm.test/));
    const enableDialog = await screen.findByRole("dialog", { name: /Enable — disabled@farm\.test/ });
    expect(enableDialog).not.toHaveAttribute("aria-describedby");
  });

  it("opening Disable and closing the dialog fires no disableUser call", async () => {
    await renderReady(ADMIN);
    fireEvent.click(disableRow(/worker@farm.test/));

    const dialog = await screen.findByRole("dialog", { name: /Disable — worker@farm\.test/ });
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockStepUp).not.toHaveBeenCalled();
    expect(mockDisableUser).not.toHaveBeenCalled();
  });

  it("submitting with the reason left empty sends reason: null — the optional-reason regression", async () => {
    mockStepUp.mockResolvedValue({ token: "grant-d1", expiresAt: "2026-01-01T00:05:00Z" });
    mockDisableUser.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    fireEvent.click(disableRow(/worker@farm.test/));
    const dialog = await screen.findByRole("dialog", { name: /Disable — worker@farm\.test/ });
    // The reason textarea is left untouched — blank — and the dialog still
    // submits: a mandatory reason was the bug (#356), so this is the case
    // that must pass without ever being forced to type anything.
    fireEvent.change(within(dialog).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => {
      fireEvent.click(within(dialog).getByRole("button", { name: "Disable" }));
    });

    expect(mockStepUp).toHaveBeenCalledWith(OWNER_STEP_UP_PASSWORD);
    expect(mockDisableUser).toHaveBeenCalledWith(
      "u-w", { reason: null }, expect.any(String), "grant-d1");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(await screen.findByText(/worker@farm\.test has been disabled/)).toBeInTheDocument();
    expect(mockListUsers).toHaveBeenCalledTimes(2); // initial load + post-disable refresh
  });

  it("submitting with a reason sends that exact trimmed string", async () => {
    mockStepUp.mockResolvedValue({ token: "grant-d1", expiresAt: "2026-01-01T00:05:00Z" });
    mockDisableUser.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    fireEvent.click(disableRow(/worker@farm.test/));
    const dialog = await screen.findByRole("dialog", { name: /Disable — worker@farm\.test/ });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), {
      target: { value: "  No longer works here  " },
    });
    fireEvent.change(within(dialog).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => {
      fireEvent.click(within(dialog).getByRole("button", { name: "Disable" }));
    });

    expect(mockDisableUser).toHaveBeenCalledWith(
      "u-w", { reason: "No longer works here" }, expect.any(String), "grant-d1");
  });

  it("enabling a disabled user opens the shared dialog directly, with no reason field", async () => {
    mockListUsers.mockResolvedValue([DISABLED_USER]);
    mockStepUp.mockResolvedValue({ token: "grant-e1", expiresAt: "2026-01-01T00:05:00Z" });
    mockEnableUser.mockResolvedValue(undefined);
    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText("disabled@farm.test");

    fireEvent.click(enableRow(/disabled@farm.test/));
    const stepUpDialog = await screen.findByRole("dialog", { name: /Enable — disabled@farm\.test/ });
    expect(within(stepUpDialog).queryByLabelText(/Reason/)).not.toBeInTheDocument();
    fireEvent.change(within(stepUpDialog).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => {
      fireEvent.click(within(stepUpDialog).getByRole("button", { name: "Enable" }));
    });

    expect(mockStepUp).toHaveBeenCalledWith(OWNER_STEP_UP_PASSWORD);
    expect(mockEnableUser).toHaveBeenCalledWith("u-d", expect.any(String), "grant-e1");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(await screen.findByText(/disabled@farm\.test has been re-enabled/)).toBeInTheDocument();
  });

  it("keeps the dialog open and shows the error in the shared error slot when a disable is rejected", async () => {
    mockStepUp.mockResolvedValue({ token: "grant-d2", expiresAt: "2026-01-01T00:05:00Z" });
    mockDisableUser.mockRejectedValue(
      new ApiError(422, "Users.LastOwner", "Cannot disable the sole remaining owner."));
    await renderReady(ADMIN);

    fireEvent.click(disableRow(/worker@farm.test/));
    const dialog = await screen.findByRole("dialog", { name: /Disable — worker@farm\.test/ });
    fireEvent.change(within(dialog).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => {
      fireEvent.click(within(dialog).getByRole("button", { name: "Disable" }));
    });

    // Renders through the "disable-enable" scope of useDialogErrors (#491
    // merge, #492 round-4 local review) — same DialogError component every
    // other dialog on this screen uses.
    expect(within(dialog).getByText(/sole remaining owner/)).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(mockEnableUser).not.toHaveBeenCalled();
  });

  // Local review of the #491 merge (round-4 of #492): both modes share ONE
  // error scope ("disable-enable") since it's one dialog with a swapped
  // title, not two. openStepUp only abandoned that scope on a DIFFERENT
  // user, so a same-user reopen that flips MODE without ever closing (the
  // row's button label follows u.disabledAt, which a background listUsers()
  // refresh — triggered here by an unrelated edit — can flip while this
  // dialog is still open) would otherwise carry the failed disable's error
  // text into the enable dialog: a message about the wrong operation.
  it("does not carry a failed disable's error into an enable dialog reopened for the same user", async () => {
    mockListUsers.mockResolvedValueOnce([WORKER_USER, ADMIN_USER]);
    await renderReady(ADMIN);

    fireEvent.click(disableRow(/worker@farm.test/));
    const dialog = await screen.findByRole("dialog", { name: /Disable — worker@farm\.test/ });
    fireEvent.change(within(dialog).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    mockStepUp.mockResolvedValue({ token: "grant-d3", expiresAt: "2026-01-01T00:05:00Z" });
    mockDisableUser.mockRejectedValue(
      new ApiError(422, "Users.LastOwner", "Cannot disable the sole remaining owner."));
    await act(async () => {
      fireEvent.click(within(dialog).getByRole("button", { name: "Disable" }));
    });
    expect(within(dialog).getByText(/sole remaining owner/)).toBeInTheDocument();

    // Simulate a concurrent external disable of the SAME user: the next
    // listUsers() refresh (triggered here by an unrelated, successful edit
    // on a different row) reflects it, flipping worker's row to Enable —
    // the disable dialog above stays open throughout; nothing closed it.
    mockListUsers.mockResolvedValueOnce([
      { ...WORKER_USER, disabledAt: "2026-08-09T12:00:00Z" }, ADMIN_USER,
    ]);
    mockUpdateUser.mockResolvedValue(undefined);
    fireEvent.click(within(screen.getByRole("row", { name: /boss@farm.test/ }))
      .getByRole("button", { name: /edit/i }));
    const editDialog = await screen.findByRole("dialog", { name: /boss@farm\.test/ });
    await act(async () => {
      fireEvent.click(within(editDialog).getByRole("button", { name: "Save" }));
    });
    expect(await within(screen.getByRole("row", { name: /worker@farm.test/ }))
      .findByRole("button", { name: /enable/i })).toBeInTheDocument();

    // The still-open dialog is still titled Disable, still showing the old
    // error — reopening it is a fresh user action, not automatic.
    expect(screen.getByRole("dialog", { name: /Disable — worker@farm\.test/ })).toBeInTheDocument();

    fireEvent.click(within(screen.getByRole("row", { name: /worker@farm.test/ }))
      .getByRole("button", { name: /enable/i }));
    const enableDialog = await screen.findByRole("dialog", { name: /Enable — worker@farm\.test/ });
    expect(within(enableDialog).queryByText(/sole remaining owner/)).not.toBeInTheDocument();
  });

  // NOT a regression test for the #356 reason/await reorder — see the report
  // to whoever asked for this file for the full reasoning. Short version: the
  // canonical shape for this kind of race elsewhere in the file ("discards a
  // stale refresh from a worker whose dialog was closed and reopened for
  // another", flock scoping above) doesn't reach here. The row's
  // disable/enable buttons are `disabled={busy}` for the WHOLE flight (stepUp
  // + disableUser + the listUsers refresh — confirmed directly: clicking a
  // different row's trigger while one is pending fires no handler, matching
  // Dialog.tsx's own comment that a busy save "leaves its row trigger
  // disabled for one more render"), so a second dialog for another worker
  // can never open while one is in flight, and reopening for a different
  // TARGET is the only way `disableReason` could plausibly carry someone
  // else's text — `onSubmitStepUp` closes over `disableReason` fresh on every
  // render, so a value read later in the SAME invocation is identical to one
  // read earlier regardless of any retyping into the still-open dialog in the
  // meantime (confirmed by reverting the fix's line order locally: the
  // suite's outcome for this exact test was unchanged — see report). This
  // test instead pins the resulting, still-true behavior: the reason actually
  // sent is the one present at submit time, not whatever the field holds by
  // the time the write resolves.
  it("sends the reason present at submit time, not a later edit made to the still-open dialog while the write is in flight", async () => {
    const gate = deferred<{ token: string; expiresAt: string }>();
    mockStepUp.mockReturnValue(gate.promise);
    mockDisableUser.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    fireEvent.click(disableRow(/worker@farm.test/));
    const dlg = await screen.findByRole("dialog", { name: /Disable — worker@farm\.test/ });
    fireEvent.change(within(dlg).getByLabelText(/Reason/), { target: { value: "first reason" } });
    fireEvent.change(within(dlg).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });

    // Submit — stepUp() hangs on the deferred, so the write is now in flight.
    await act(async () => {
      fireEvent.click(within(dlg).getByRole("button", { name: "Disable" }));
    });

    // While still pending, retype the SAME still-open dialog's reason field.
    // Pre-fix, reading `disableReason` after the step-up await would pick
    // this up and file it as worker@farm.test's disable reason.
    fireEvent.change(within(dlg).getByLabelText(/Reason/), { target: { value: "second reason" } });

    await act(async () => {
      gate.resolve({ token: "grant-d1", expiresAt: "2026-01-01T00:05:00Z" });
    });

    expect(mockDisableUser).toHaveBeenCalledWith(
      "u-w", { reason: "first reason" }, expect.any(String), "grant-d1");
  });

  // Every other mutation on this screen has a replay/rotate test; disable did
  // not. Mirrors "replays the SAME create key after a failure, and rotates it
  // after success" above.
  it("replays the SAME disable key after a failure, and rotates it after success", async () => {
    // Round-2 review (#492) caught the first version of this test: attempt 3
    // disabled a DIFFERENT user, so its key came from a DIFFERENT scope
    // (`disable:u-a` vs `disable:u-w`) and would differ from k2 whether or not
    // a real success ever rotates anything — deleting clearKey(scope) entirely
    // left it green. Disable can't be resubmitted on the SAME target once it
    // succeeds (the row flips to Enable), so proving rotation on the disable
    // scope specifically means going disable -> enable -> disable again on one
    // user, and controlling each refresh so the row actually flips back.
    const worker = WORKER_USER;
    const workerDisabled: User = { ...worker, disabledAt: "2026-08-05T00:00:00Z" };
    mockListUsers
      .mockResolvedValueOnce([worker, ADMIN_USER]) // initial load
      .mockResolvedValueOnce([workerDisabled, ADMIN_USER]) // after the successful disable
      .mockResolvedValueOnce([worker, ADMIN_USER]); // after the enable — Disable is back
    mockStepUp.mockResolvedValue({ token: "grant-d1", expiresAt: "2026-01-01T00:05:00Z" });
    mockDisableUser.mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    mockDisableUser.mockResolvedValue(undefined);
    mockEnableUser.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    fireEvent.click(disableRow(/worker@farm.test/));
    const dlg = await screen.findByRole("dialog", { name: /Disable — worker@farm\.test/ });
    fireEvent.change(within(dlg).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    const submit = () => within(dlg).getByRole("button", { name: "Disable" });

    // Attempt 1 — fails, so the key is kept and the dialog stays open. The
    // step-up password is cleared unconditionally the instant it's captured
    // (#308 — read-then-clear-before-await), win or lose, so it must be
    // retyped for the retry below; that's independent of the idempotency key.
    await act(async () => { fireEvent.click(submit()); });
    expect(within(dlg).getByText(/Server error|boom/)).toBeInTheDocument();

    // Attempt 2 — same target/scope, refilled password → replay of the kept key.
    fireEvent.change(within(dlg).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => { fireEvent.click(submit()); });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(await screen.findByText(/worker@farm\.test has been disabled/)).toBeInTheDocument();

    const k1 = mockDisableUser.mock.calls[0][2];
    const k2 = mockDisableUser.mock.calls[1][2];
    expect(k2).toBe(k1); // failure kept the key → exact replay

    // Re-enable the SAME user, so the row offers Disable again.
    fireEvent.click(enableRow(/worker@farm.test/));
    const enableDlg = await screen.findByRole("dialog", { name: /Enable — worker@farm\.test/ });
    fireEvent.change(within(enableDlg).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => {
      fireEvent.click(within(enableDlg).getByRole("button", { name: "Enable" }));
    });
    expect(await screen.findByText(/worker@farm\.test has been re-enabled/)).toBeInTheDocument();

    // A THIRD disable, of the SAME user — the SAME "disable:u-w" scope k2 came
    // from. Only a real post-success rotation can make this key differ from k2.
    fireEvent.click(disableRow(/worker@farm.test/));
    const dlg3 = await screen.findByRole("dialog", { name: /Disable — worker@farm\.test/ });
    fireEvent.change(within(dlg3).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => { fireEvent.click(within(dlg3).getByRole("button", { name: "Disable" })); });

    const k3 = mockDisableUser.mock.calls[2][2];
    expect(k3).not.toBe(k2); // the prior success rotated it → this write is fresh
  });

  // The existing "reason left empty" test (above) never types anything into
  // the textarea, so `disableReason || null` (missing the `.trim()`) would
  // still pass it — this pins the trim explicitly.
  it("sends reason: null for a whitespace-only reason, not the raw whitespace string", async () => {
    mockStepUp.mockResolvedValue({ token: "grant-d1", expiresAt: "2026-01-01T00:05:00Z" });
    mockDisableUser.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    fireEvent.click(disableRow(/worker@farm.test/));
    const dialog = await screen.findByRole("dialog", { name: /Disable — worker@farm\.test/ });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "   " } });
    fireEvent.change(within(dialog).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => {
      fireEvent.click(within(dialog).getByRole("button", { name: "Disable" }));
    });

    expect(mockDisableUser).toHaveBeenCalledWith(
      "u-w", { reason: null }, expect.any(String), "grant-d1");
  });
});

describe("UsersPage dialog dismissal", () => {
  it("closes the create dialog on Cancel without writing", async () => {
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "nope@farm.test" } });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockCreateUser).not.toHaveBeenCalled();
  });

  // #314 — the typed plaintext password used to survive every close path
  // except a successful submit, and reappear when the dialog was reopened.
  const passwordInput = () => within(dialog()).getByLabelText(/Password/);
  // Runtime-generated so no literal secret lands in source (GitGuardian).
  const typeCreatePassword = () => {
    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "leaky@farm.test" } });
    fireEvent.change(passwordInput(), { target: { value: `pw-${crypto.randomUUID()}` } });
  };

  it("clears the typed password on Cancel, so reopening the dialog shows it empty (#314)", async () => {
    await renderReady(ADMIN);
    openCreate();
    typeCreatePassword();

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    openCreate();
    expect(passwordInput()).toHaveValue("");
  });

  // #314 review — password wasn't the only state that leaked across a close.
  // An abandoned "Admin" selection stayed selected on reopen, so an operator
  // who believed they were starting a fresh entry could grant admin by
  // accident. The whole form resets, not just the credential field.
  it("resets an abandoned Admin role selection, so reopening never pre-grants admin (#314)", async () => {
    await renderReady(ADMIN);
    openCreate();
    typeCreatePassword();
    fireEvent.change(within(dialog()).getByLabelText(/Role/), { target: { value: "Admin" } });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    openCreate();
    expect(within(dialog()).getByLabelText(/Role/)).toHaveValue("Worker");
    expect(within(dialog()).getByLabelText("Email *")).toHaveValue("");
  });

  it("clears the typed password on the close (X) button, so reopening the dialog shows it empty (#314)", async () => {
    await renderReady(ADMIN);
    openCreate();
    typeCreatePassword();

    fireEvent.click(within(dialog()).getByRole("button", { name: "Close" }));

    openCreate();
    expect(passwordInput()).toHaveValue("");
  });

  it("clears the typed password on Escape, so reopening the dialog shows it empty (#314)", async () => {
    await renderReady(ADMIN);
    openCreate();
    typeCreatePassword();

    fireEvent.keyDown(document, { key: "Escape" });

    openCreate();
    expect(passwordInput()).toHaveValue("");
  });

  it("clears the typed password on a backdrop click, so reopening the dialog shows it empty (#314)", async () => {
    await renderReady(ADMIN);
    openCreate();
    typeCreatePassword();

    fireEvent.click(document.querySelector(".dialog-backdrop")!);

    openCreate();
    expect(passwordInput()).toHaveValue("");
  });

  it("clears the typed password once creation succeeds, before/as the dialog unmounts (#314)", async () => {
    mockCreateUser.mockResolvedValue({ id: "u-new" });
    await renderReady(ADMIN);
    openCreate();
    typeCreatePassword();
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });

    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" }));
    });

    openCreate();
    expect(passwordInput()).toHaveValue("");
  });

  it("clears the typed password if the Users route unmounts while the dialog is open, so a fresh mount never shows it (#314)", async () => {
    // Stands in for a route change: toggling this flag mounts/unmounts a
    // real UsersPage instance, the same lifecycle react-router drives when
    // navigating away from and back to /users.
    function ToggleHarness() {
      const [show, setShow] = useState(true);
      return (
        <>
          <button onClick={() => setShow((s) => !s)}>toggle users page</button>
          {show && <UsersPage />}
        </>
      );
    }
    renderWithProviders(<ToggleHarness />, { token: ADMIN });
    await screen.findByText("worker@farm.test");
    openCreate();
    typeCreatePassword();

    fireEvent.click(screen.getByRole("button", { name: "toggle users page" })); // unmount
    fireEvent.click(screen.getByRole("button", { name: "toggle users page" })); // remount
    await screen.findByText("worker@farm.test");

    openCreate();
    expect(passwordInput()).toHaveValue("");
  });
});

describe("UsersPage flock scoping", () => {
  it("drills into a worker's assignments on 'flocks', scoped to that user", async () => {
    mockListAssignments.mockResolvedValue([ASSIGN_1]);
    await renderReady(ADMIN);

    const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
    await act(async () => {
      fireEvent.click(within(workerRow).getByRole("button", { name: "flocks" }));
    });

    expect(mockListAssignments).toHaveBeenCalledWith("u-w");
    // F133: the per-worker panel now opens in the shared dialog, titled with the
    // worker's email (its accessible name).
    const panel = await screen.findByRole("dialog", { name: /Flock access — worker@farm.test/ });
    // The assignment's flock id resolves to a name via the loaded flocks list.
    // Scope to the <li> so it doesn't collide with the dropdown's "Coop A" option.
    const item = within(panel).getByRole("listitem");
    expect(within(item).getByText("Coop A")).toBeInTheDocument();
    expect(within(item).getByRole("button", { name: "remove" })).toBeInTheDocument();
  });

  it("shows the empty-assignments hint (account-wide access) and lists only ACTIVE flocks to assign", async () => {
    mockListAssignments.mockResolvedValue([]);
    await renderReady(ADMIN);

    const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
    await act(async () => {
      fireEvent.click(within(workerRow).getByRole("button", { name: "flocks" }));
    });

    expect(await screen.findByText(/No assignments — account-wide access/)).toBeInTheDocument();
    // Archived flocks are filtered out of the assignable dropdown (only Active).
    expect(within(flockSelect()).getByRole("option", { name: "Coop A" })).toBeInTheDocument();
    expect(within(flockSelect()).getByRole("option", { name: "Coop B" })).toBeInTheDocument();
    expect(within(flockSelect()).queryByRole("option", { name: "Old Coop" })).toBeNull();
  });

  it("assigns the SELECTED flock (off the default first) to the open worker with userId, flockId, and a key", async () => {
    mockListAssignments.mockResolvedValue([]);
    mockAssignFlock.mockResolvedValue({ id: "as-new" });
    await renderReady(ADMIN);

    const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
    await act(async () => {
      fireEvent.click(within(workerRow).getByRole("button", { name: "flocks" }));
    });
    await screen.findByText(/account-wide access/);

    // Default assign selection is the first active flock (fl1); choose fl2.
    fireEvent.change(flockSelect(), { target: { value: "fl2" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Assign flock" }));
    });

    expect(mockAssignFlock).toHaveBeenCalledWith("u-w", "fl2", expect.any(String));
    // Refreshes the assignment list for the same worker afterwards.
    expect(mockListAssignments).toHaveBeenLastCalledWith("u-w");
  });

  it("replays the SAME assign key after a failure, and rotates it after success", async () => {
    mockListAssignments.mockResolvedValue([]);
    mockAssignFlock.mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    mockAssignFlock.mockResolvedValue({ id: "as-new" });
    await renderReady(ADMIN);

    const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
    await act(async () => {
      fireEvent.click(within(workerRow).getByRole("button", { name: "flocks" }));
    });
    await screen.findByText(/account-wide access/);

    // The assign scope keys off (openUser, selected flock); the default selection
    // (first active flock, fl1) is unchanged across all three clicks → same scope.
    const assign = () => screen.getByRole("button", { name: "Assign flock" });

    // Attempt 1 — fails, so the key is kept.
    await act(async () => { fireEvent.click(assign()); });
    expect(await screen.findByText(/Server error|boom/)).toBeInTheDocument();

    // Attempt 2 — resubmit the same selection → replay of the kept key.
    await act(async () => { fireEvent.click(assign()); });

    // Attempt 3 — the prior success cleared the key → a fresh one on the next write.
    await act(async () => { fireEvent.click(assign()); });

    const k1 = mockAssignFlock.mock.calls[0][2];
    const k2 = mockAssignFlock.mock.calls[1][2];
    const k3 = mockAssignFlock.mock.calls[2][2];
    expect(k2).toBe(k1); // failure kept the key → exact replay
    expect(k3).not.toBe(k2); // success rotated it → next write is fresh
  });

  it("removes an assignment with the userId, the assignment id, and a key, then refreshes to empty", async () => {
    mockListAssignments.mockResolvedValueOnce([ASSIGN_1]).mockResolvedValueOnce([]);
    mockUnassignFlock.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
    await act(async () => {
      fireEvent.click(within(workerRow).getByRole("button", { name: "flocks" }));
    });
    const item = await screen.findByRole("listitem");
    await act(async () => {
      fireEvent.click(within(item).getByRole("button", { name: "remove" }));
    });

    expect(mockUnassignFlock).toHaveBeenCalledWith("u-w", "as1", expect.any(String));
    expect(await screen.findByText(/No assignments — account-wide access/)).toBeInTheDocument();
  });

  it("resets the assign dropdown to the first active flock each time the dialog opens", async () => {
    mockListAssignments.mockResolvedValue([]);
    await renderReady(ADMIN);

    const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
    // Open, switch the pick off the default (fl1 -> fl2), then close.
    await act(async () => {
      fireEvent.click(within(workerRow).getByRole("button", { name: "flocks" }));
    });
    await screen.findByRole("dialog", { name: /Flock access/ });
    fireEvent.change(flockSelect(), { target: { value: "fl2" } });
    expect(flockSelect()).toHaveValue("fl2");
    fireEvent.click(screen.getByRole("button", { name: "Done" }));

    // Reopen — the dropdown is back on the first active flock, not the stale fl2,
    // so a distracted admin can't assign the previous worker's pick by accident.
    await act(async () => {
      fireEvent.click(within(workerRow).getByRole("button", { name: "flocks" }));
    });
    await screen.findByRole("dialog", { name: /Flock access/ });
    expect(flockSelect()).toHaveValue("fl1");
  });

  it("closes the flock dialog on Done", async () => {
    mockListAssignments.mockResolvedValue([ASSIGN_1]);
    await renderReady(ADMIN);

    const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
    await act(async () => {
      fireEvent.click(within(workerRow).getByRole("button", { name: "flocks" }));
    });
    const panel = await screen.findByRole("dialog", { name: /Flock access/ });

    fireEvent.click(within(panel).getByRole("button", { name: "Done" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("discards a stale refresh from a worker whose dialog was closed and reopened for another", async () => {
    const WORKER_2: User = {
      id: "u-w2", email: "worker2@farm.test", displayName: "Walt", role: "Worker", disabledAt: null,
    };
    mockListUsers.mockResolvedValue([WORKER_USER, WORKER_2, ADMIN_USER]);
    // Call 1: open A (has Coop A). Call 2: A's post-remove refresh — hung.
    // Call 3: open B (empty). If the guard fails, A's refresh overwrites B.
    let resolveStale!: (v: FlockAssignment[]) => void;
    mockListAssignments
      .mockResolvedValueOnce([ASSIGN_1])
      .mockImplementationOnce(() => new Promise<FlockAssignment[]>((r) => { resolveStale = r; }))
      .mockResolvedValueOnce([]);
    mockUnassignFlock.mockResolvedValue(undefined);

    renderWithProviders(<UsersPage />, { token: ADMIN });
    await screen.findByText("worker@farm.test");

    // Open worker A, remove its assignment — the refresh now hangs (busy).
    const rowA = screen.getByRole("row", { name: /worker@farm\.test/ });
    await act(async () => {
      fireEvent.click(within(rowA).getByRole("button", { name: "flocks" }));
    });
    const panelA = await screen.findByRole("dialog", { name: /Flock access — worker@farm\.test/ });
    await act(async () => {
      fireEvent.click(within(panelA).getByRole("button", { name: "remove" }));
    });

    // Close A and open worker B while A's refresh is still in flight.
    fireEvent.click(within(panelA).getByRole("button", { name: "Done" }));
    const rowB = screen.getByRole("row", { name: /worker2@farm\.test/ });
    await act(async () => {
      fireEvent.click(within(rowB).getByRole("button", { name: "flocks" }));
    });
    const panelB = await screen.findByRole("dialog", { name: /Flock access — worker2@farm\.test/ });
    expect(within(panelB).getByText(/No assignments — account-wide access/)).toBeInTheDocument();

    // A's refresh finally resolves — it must NOT splice A's list into B's dialog.
    await act(async () => { resolveStale([ASSIGN_1]); });
    expect(within(panelB).queryByRole("listitem")).toBeNull();
    expect(within(panelB).getByText(/No assignments — account-wide access/)).toBeInTheDocument();
  });

  it("surfaces an error when an assign fails, keeping the panel open", async () => {
    mockListAssignments.mockResolvedValue([]);
    mockAssignFlock.mockRejectedValue(new ApiError(409, "Conflict", "already assigned"));
    await renderReady(ADMIN);

    const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
    await act(async () => {
      fireEvent.click(within(workerRow).getByRole("button", { name: "flocks" }));
    });
    await screen.findByText(/account-wide access/);
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Assign flock" }));
    });

    expect(await screen.findByText(/already assigned|Conflict/)).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument(); // dialog stayed open
  });
});

// #236 — busy state swapped for the shared usePendingAction. Held flights
// (deferred promises, client.test.ts idiom) pin that exactly the clicked
// trigger spins while every sibling verb merely disables.
describe("UsersPage pending states (#236)", () => {
  function deferred<T>() {
    let resolve!: (v: T) => void;
    let reject!: (e: unknown) => void;
    const promise = new Promise<T>((res, rej) => {
      resolve = res;
      reject = rej;
    });
    return { promise, resolve, reject };
  }

  it("spins the create submit while its flight is open, then closes clean", async () => {
    const gate = deferred<{ id: string }>();
    mockCreateUser.mockReturnValue(gate.promise);
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "held@farm.test" } });
    fireEvent.change(within(dialog()).getByLabelText(/Password/), { target: { value: `pw-${crypto.randomUUID()}` } });
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" }));
    });

    const submit = within(dialog()).getByRole("button", { name: "Create user" });
    expect(submit).toHaveAttribute("aria-busy", "true");
    expect(submit).toBeDisabled();

    await act(async () => { gate.resolve({ id: "u-new" }); });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(document.querySelector('[aria-busy="true"]')).toBeNull();
    expect(await screen.findByText(/account created for held@farm\.test/)).toBeInTheDocument();
  });

  it("spins only the removed assignment's own verb; the sibling rows and Assign merely disable", async () => {
    const ASSIGN_2: FlockAssignment = { id: "as2", flockId: "fl2" };
    mockListAssignments.mockResolvedValue([ASSIGN_1, ASSIGN_2]);
    const gate = deferred<void>();
    mockUnassignFlock.mockReturnValue(gate.promise);
    await renderReady(ADMIN);

    const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
    await act(async () => {
      fireEvent.click(within(workerRow).getByRole("button", { name: "flocks" }));
    });
    const items = await screen.findAllByRole("listitem");
    await act(async () => {
      fireEvent.click(within(items[0]).getByRole("button", { name: "remove" }));
    });

    const rows = screen.getAllByRole("listitem");
    const removeA = within(rows[0]).getByRole("button", { name: "remove" });
    expect(removeA).toHaveAttribute("aria-busy", "true");
    expect(removeA).toBeDisabled();
    const removeB = within(rows[1]).getByRole("button", { name: "remove" });
    expect(removeB).toBeDisabled();
    expect(removeB).not.toHaveAttribute("aria-busy");
    const assignButton = screen.getByRole("button", { name: "Assign flock" });
    expect(assignButton).toBeDisabled();
    expect(assignButton).not.toHaveAttribute("aria-busy");
    // The flock select embeds the selection in the assign scope: changing it
    // mid-flight would re-point isPending at a scope nobody runs and drop
    // the spinner while the request is open — so it locks with the flight.
    expect(screen.getByRole("combobox")).toBeDisabled();

    await act(async () => { gate.resolve(); });
    expect(document.querySelector('[aria-busy="true"]')).toBeNull();
    expect(screen.getByRole("button", { name: "Assign flock" })).toBeEnabled();
    expect(screen.getByRole("combobox")).toBeEnabled();
  });
});

describe("UsersPage role gating", () => {
  // The admin-only gate for this screen lives OUTSIDE the component: AppLayout
  // renders the /users nav link only when role === "Admin", and ProtectedRoute
  // plus the API enforce it. UsersPage itself has no useAuth check, so it renders
  // identically for any authenticated session. Asserted here so a future
  // in-component gate that silently hid the form would trip this test.
  it("UsersPage does not self-gate — renders for any authenticated role", async () => {
    await renderReady(WORKER);
    expect(screen.getByRole("button", { name: "New user" })).toBeInTheDocument();
    openCreate();
    expect(within(dialog()).getByLabelText("Email *")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 22, batch B4)
// ---------------------------------------------------------------------------

// `users` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting it, even under a non-English locale, would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use, so each
// marker only renders if the screen actually reads the catalog rather than a
// literal that happens to still match it.
describe("UsersPage i18n wiring (#182, Task 22)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the heading from the catalog, not a hardcoded literal", async () => {
    await withOverride("users", "heading", "HEADING-MARKER", async () => {
      await renderReady(ADMIN);
      expect(screen.getByRole("heading", { name: "HEADING-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Users" })).not.toBeInTheDocument();
    });
  });

  it("reads the New-user button label and reuses it as the create-dialog title, not a hardcoded literal", async () => {
    await withOverride("users", "newUserButton", "NEW-USER-MARKER", async () => {
      await renderReady(ADMIN);
      const trigger = screen.getByRole("button", { name: /NEW-USER-MARKER/ });
      expect(trigger).toBeInTheDocument();
      fireEvent.click(trigger);
      expect(await screen.findByRole("heading", { name: "NEW-USER-MARKER" })).toBeInTheDocument();
      expect(screen.queryByText("New user")).not.toBeInTheDocument();
    });
  });

  it("reads the role-description prose from the catalog, not a hardcoded literal", async () => {
    await withOverride("users", "roleDescription", "ROLE-DESC-MARKER", async () => {
      await renderReady(ADMIN);
      expect(screen.getByText("ROLE-DESC-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/Workers record the day/)).not.toBeInTheDocument();
    });
  });

  it("reads the Email table column header from the catalog, not a hardcoded literal", async () => {
    await withOverride("users", "emailColumnHeader", "EMAIL-HEADER-MARKER", async () => {
      await renderReady(ADMIN);
      expect(screen.getByText("EMAIL-HEADER-MARKER")).toBeInTheDocument();
    });
  });

  it("reads the row 'edit' button label from the catalog, not a hardcoded literal", async () => {
    await withOverride("users", "editButton", "EDIT-MARKER", async () => {
      await renderReady(ADMIN);
      const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
      expect(within(workerRow).getByRole("button", { name: /EDIT-MARKER/ })).toBeInTheDocument();
      expect(within(workerRow).queryByRole("button", { name: /^edit$/ })).not.toBeInTheDocument();
    });
  });

  it("interpolates the worker's email into the flock-access dialog title from the catalog", async () => {
    await withOverride("users", "flockAccessTitle", "FLOCK-MARKER {{email}} MARKER-END", async () => {
      await renderReady(ADMIN);
      const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
      await act(async () => {
        fireEvent.click(within(workerRow).getByRole("button", { name: "flocks" }));
      });
      expect(
        await screen.findByRole("dialog", { name: "FLOCK-MARKER worker@farm.test MARKER-END" }),
      ).toBeInTheDocument();
    });
  });

  it("reads the no-assignments message from the catalog, not a hardcoded literal", async () => {
    mockListAssignments.mockResolvedValue([]);
    await withOverride("users", "noAssignmentsMessage", "NO-ASSIGN-MARKER", async () => {
      await renderReady(ADMIN);
      const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
      await act(async () => {
        fireEvent.click(within(workerRow).getByRole("button", { name: "flocks" }));
      });
      expect(await screen.findByText("NO-ASSIGN-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/account-wide access/)).not.toBeInTheDocument();
    });
  });

  it("reads the Create-user submit button from the catalog, not a hardcoded literal", async () => {
    await withOverride("users", "createUserButton", "CREATE-MARKER", async () => {
      await renderReady(ADMIN);
      openCreate();
      expect(within(dialog()).getByRole("button", { name: "CREATE-MARKER" })).toBeInTheDocument();
    });
  });

  // The create-success message is built with the imperative i18n.t() (onCreate
  // is an event handler, not render — see CONTRIBUTING-i18n.md's imperative
  // i18n.t() pattern). Also proves {{role}} carries roleLabel(role) — the
  // THIRD roleLabel site — rather than the raw wire value: picking ReadOnly
  // renders "Read-only" in the interpolated slot.
  it("interpolates roleLabel(role) and the email into the create-success message from the catalog", async () => {
    mockCreateUser.mockResolvedValue({ id: "u-new" });
    await withOverride(
      "users", "createSuccessMessage", "CREATED-MARKER {{role}}/{{email}} MARKER-END",
      async () => {
        await renderReady(ADMIN);
        openCreate();
        fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "ro@farm.test" } });
        fireEvent.change(within(dialog()).getByLabelText(/Password/), { target: { value: `pw-${crypto.randomUUID()}` } });
        fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "ReadOnly" } });
        fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
          target: { value: OWNER_STEP_UP_PASSWORD },
        });
        await act(async () => {
          fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" }));
        });
        expect(
          await screen.findByText("CREATED-MARKER Read-only/ro@farm.test MARKER-END"),
        ).toBeInTheDocument();
      },
    );
  });

  // Built with the imperative i18n.t() (onSetPassword's mismatch guard runs in
  // an event handler, not render).
  it("reads the password-mismatch message from the catalog, not a hardcoded literal", async () => {
    await withOverride("users", "passwordMismatchMessage", "MISMATCH-MARKER", async () => {
      await renderReady(ADMIN);
      const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
      fireEvent.click(within(workerRow).getByRole("button", { name: "password" }));
      fireEvent.change(within(dialog()).getByLabelText(/New password/), { target: { value: "aaaaaaaaaaaa" } });
      fireEvent.change(within(dialog()).getByLabelText(/Confirm new password/), { target: { value: "bbbbbbbbbbbb" } });
      fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
        target: { value: OWNER_STEP_UP_PASSWORD },
      });
      await act(async () => {
        fireEvent.click(within(dialog()).getByRole("button", { name: "Set password" }));
      });
      expect(within(dialog()).getByText("MISMATCH-MARKER")).toBeInTheDocument();
      expect(within(dialog()).queryByText(/don't match/i)).not.toBeInTheDocument();
    });
  });

  // Built with the imperative i18n.t() (onSetPassword's success branch runs in
  // an event handler).
  it("interpolates the email into the password-set success message from the catalog", async () => {
    mockSetUserPassword.mockResolvedValue(undefined);
    await withOverride(
      "users", "passwordSetMessage", "PW-SET-MARKER {{email}} MARKER-END",
      async () => {
        await renderReady(ADMIN);
        const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
        fireEvent.click(within(workerRow).getByRole("button", { name: "password" }));
        const password = `pw-${crypto.randomUUID()}`;
        fireEvent.change(within(dialog()).getByLabelText(/New password/), { target: { value: password } });
        fireEvent.change(within(dialog()).getByLabelText(/Confirm new password/), { target: { value: password } });
        fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
          target: { value: OWNER_STEP_UP_PASSWORD },
        });
        await act(async () => {
          fireEvent.click(within(dialog()).getByRole("button", { name: "Set password" }));
        });
        expect(await screen.findByText("PW-SET-MARKER worker@farm.test MARKER-END")).toBeInTheDocument();
      },
    );
  });

  // Built with the imperative i18n.t() (onUpdate's success branch runs in an
  // event handler).
  it("interpolates the email into the updated-user message from the catalog", async () => {
    mockUpdateUser.mockResolvedValue(undefined);
    await withOverride("users", "updatedMessage", "UPDATED-MARKER {{email}} MARKER-END", async () => {
      await renderReady(ADMIN);
      const workerRow = screen.getByRole("row", { name: /worker@farm.test/ });
      fireEvent.click(within(workerRow).getByRole("button", { name: "edit" }));
      fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "New Name" } });
      await act(async () => {
        fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
      });
      expect(await screen.findByText("UPDATED-MARKER worker@farm.test MARKER-END")).toBeInTheDocument();
    });
  });

  // Proves the Admin picker option's "(owner)" wrapper reads BOTH the `users`
  // catalog string AND roleLabel("Admin") — not two hardcoded literals that
  // happen to concatenate to "Admin (owner)".
  it("interpolates roleLabel('Admin') into the Admin picker option from the catalog", async () => {
    await withOverride("users", "adminRoleOption", "ADMIN-MARKER {{label}} MARKER-END", async () => {
      await renderReady(ADMIN);
      openCreate();
      const select = within(dialog()).getByLabelText("Role");
      expect(within(select).getByRole("option", { name: "ADMIN-MARKER Admin MARKER-END" })).toBeInTheDocument();
      expect(within(select).queryByRole("option", { name: "Admin (owner)" })).not.toBeInTheDocument();
    });
  });

  // Proves BOTH role sites — the table cell (roleLabel(u.role)) and the
  // picker option text (roleLabel(v)) — read the SAME enums:role.ReadOnly
  // catalog entry, not a hardcoded "Read-only" literal at either site.
  it("reads the ReadOnly role label from the enums catalog at both the table cell and the picker option", async () => {
    mockListUsers.mockResolvedValue([WORKER_USER, ADMIN_USER, READONLY_USER]);
    await withOverride("enums", "role.ReadOnly", "READONLY-MARKER", async () => {
      await renderReady(ADMIN);

      const roRow = screen.getByRole("row", { name: /ro@farm.test/ });
      expect(within(roRow).getByText("READONLY-MARKER")).toBeInTheDocument();
      expect(within(roRow).queryByText("ReadOnly")).not.toBeInTheDocument();
      expect(within(roRow).queryByText("Read-only")).not.toBeInTheDocument();

      openCreate();
      const select = within(dialog()).getByLabelText("Role");
      expect(within(select).getByRole("option", { name: "READONLY-MARKER" })).toBeInTheDocument();
      expect(within(select).queryByRole("option", { name: "Read-only" })).not.toBeInTheDocument();
    });
  });
});

// #308/#360 — step-up re-confirmation is unconditional for interactive user
// creation, administrative password reset, and role changes. This block pins
// grant attachment plus the SPA's proof-state lifecycle: never store the
// password, clear it before awaiting issuance, and clear it on close/logout.
describe("UsersPage step-up authentication (#308)", () => {
  const ownerPasswordInput = () => within(dialog()).getByLabelText(/Your current password/);
  const createPasswordInput = () => within(dialog()).getByLabelText(/Password \(min 12 chars\)/);
  const selectAdminRole = () =>
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Admin" } });
  const openPwFor = (rowName: RegExp) =>
    fireEvent.click(within(screen.getByRole("row", { name: rowName })).getByRole("button", { name: "password" }));

  it("always shows the required step-up field, including for the default Worker role", async () => {
    await renderReady(ADMIN);
    openCreate();

    expect(ownerPasswordInput()).toBeRequired();
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Manager" } });
    expect(ownerPasswordInput()).toBeRequired();

    selectAdminRole();
    expect(ownerPasswordInput()).toBeRequired();
  });

  it("success: creating another Owner exchanges the current password for a grant and attaches it", async () => {
    mockStepUp.mockResolvedValue({ token: "grant-123", expiresAt: "2026-01-01T00:05:00Z" });
    mockCreateUser.mockResolvedValue({ id: "u-new" });
    await renderReady(ADMIN);
    openCreate();

    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "boss@farm.test" } });
    fireEvent.change(createPasswordInput(), { target: { value: `pw-${crypto.randomUUID()}` } });
    selectAdminRole();
    fireEvent.change(ownerPasswordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" })); });

    expect(mockStepUp).toHaveBeenCalledWith(OWNER_STEP_UP_PASSWORD);
    expect(mockCreateUser.mock.calls[0][2]).toBe("grant-123"); // the grant, as the 3rd arg
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(await screen.findByText(/account created for boss@farm\.test/i)).toBeInTheDocument();
  });

  it("never stores the entered step-up password: reopening after a successful use shows it empty", async () => {
    mockStepUp.mockResolvedValue({ token: "grant-123", expiresAt: "2026-01-01T00:05:00Z" });
    mockCreateUser.mockResolvedValue({ id: "u-new" });
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "boss2@farm.test" } });
    fireEvent.change(createPasswordInput(), { target: { value: `pw-${crypto.randomUUID()}` } });
    selectAdminRole();
    fireEvent.change(ownerPasswordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" })); });

    openCreate();
    selectAdminRole();
    expect(ownerPasswordInput()).toHaveValue("");
  });

  // #336/#360 — switching roles never disables proof. The password is cleared
  // before issuance and remains cleared when the successful dialog is reopened.
  it("still spends and clears step-up when the role is switched away from Owner", async () => {
    mockCreateUser.mockResolvedValue({ id: "u-new" });
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "worker@farm.test" } });
    fireEvent.change(createPasswordInput(), { target: { value: `pw-${crypto.randomUUID()}` } });

    // Pick Owner, type the proof password, then change your mind.
    selectAdminRole();
    fireEvent.change(ownerPasswordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Worker" } });

    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" })); });

    expect(mockStepUp).toHaveBeenCalledWith(OWNER_STEP_UP_PASSWORD);
    expect(mockCreateUser.mock.calls[0][2]).toBe("grant-default");

    openCreate();
    selectAdminRole();
    expect(ownerPasswordInput()).toHaveValue("");
  });

  it("cancel: closing the create dialog makes no step-up or create call, and clears the field", async () => {
    await renderReady(ADMIN);
    openCreate();
    selectAdminRole();
    fireEvent.change(ownerPasswordInput(), { target: { value: "typed-but-abandoned" } });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(mockStepUp).not.toHaveBeenCalled();
    expect(mockCreateUser).not.toHaveBeenCalled();

    openCreate();
    selectAdminRole();
    expect(ownerPasswordInput()).toHaveValue("");
  });

  it("server rejection: a wrong current password refuses the grant, keeps the dialog open, and creates no user", async () => {
    mockStepUp.mockRejectedValue(
      new ApiError(401, "Users.CurrentPasswordIncorrect", "Current password is incorrect."));
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "boss3@farm.test" } });
    fireEvent.change(createPasswordInput(), { target: { value: `pw-${crypto.randomUUID()}` } });
    selectAdminRole();
    fireEvent.change(ownerPasswordInput(), { target: { value: "wrong-password" } });

    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" })); });

    expect(within(dialog()).getByText(/Current password is incorrect/)).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(mockCreateUser).not.toHaveBeenCalled();
  });

  it("expiry: a grant the server refuses as no-longer-valid keeps the dialog open and creates no user", async () => {
    mockStepUp.mockResolvedValue({ token: "stale-grant", expiresAt: "2026-01-01T00:05:00Z" });
    mockCreateUser.mockRejectedValue(
      new ApiError(403, "Identity.StepUpRequired", "Recent re-authentication is required for this action."));
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "boss4@farm.test" } });
    fireEvent.change(createPasswordInput(), { target: { value: `pw-${crypto.randomUUID()}` } });
    selectAdminRole();
    fireEvent.change(ownerPasswordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });

    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" })); });

    expect(within(dialog()).getByText(/re-authentication is required/i)).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("requires the step-up field when resetting a non-Owner's password", async () => {
    await renderReady(ADMIN);
    openPwFor(/worker@farm.test/);
    expect(ownerPasswordInput()).toBeRequired();
  });

  it("resetting an Owner's password prompts for the step-up field and attaches the grant", async () => {
    mockStepUp.mockResolvedValue({ token: "grant-456", expiresAt: "2026-01-01T00:05:00Z" });
    mockSetUserPassword.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    openPwFor(/boss@farm.test/);
    expect(ownerPasswordInput()).toBeInTheDocument();

    const newPw = `Aa1!${crypto.randomUUID()}`;
    fireEvent.change(within(dialog()).getByLabelText(/New password/), { target: { value: newPw } });
    fireEvent.change(within(dialog()).getByLabelText(/Confirm new password/), { target: { value: newPw } });
    fireEvent.change(ownerPasswordInput(), { target: { value: OWNER_STEP_UP_PASSWORD } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Set password" })); });

    expect(mockStepUp).toHaveBeenCalledWith(OWNER_STEP_UP_PASSWORD);
    expect(mockSetUserPassword).toHaveBeenCalledWith(
      "u-a", { newPassword: newPw }, expect.any(String), "grant-456");
  });

  it("cancel on the reset dialog makes no step-up or set-password call", async () => {
    await renderReady(ADMIN);
    openPwFor(/boss@farm.test/);
    fireEvent.change(ownerPasswordInput(), { target: { value: "typed-but-abandoned" } });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(mockStepUp).not.toHaveBeenCalled();
    expect(mockSetUserPassword).not.toHaveBeenCalled();
  });

  // #308 acceptance criteria — "clears proof state on logout". A controlled
  // AuthContext (rather than the real AuthProvider renderWithProviders uses)
  // lets this test flip isAuthenticated directly, in place, so the dialog
  // stays mounted (its LOCAL state untouched by the rerender) and the field
  // is directly observable — proving the effect cleared it, not incidental
  // unmounting.
  it("clears any half-entered step-up password the instant the session ends", async () => {
    const me: Me = {
      id: "u1", email: "test@farm.local", name: null, role: "Admin", language: null,
      preferredStepperUnit: null,
    };
    const tree = (isAuthenticated: boolean) => (
      <MemoryRouter initialEntries={["/"]}>
        <AuthContext.Provider value={{
          isAuthenticated, isLoading: false, isAdmin: true, role: "Admin" as Role, userId: "u1",
          // #283 — an Owner working the Users screen is already past the
          // first-run set-password gate.
          mustChangePassword: false,
          unauthenticatedReason: null,
          login: vi.fn(), logout: vi.fn(),
        }}
        >
          <MeContext.Provider value={me}>
            <FarmContext.Provider value={farmState({ farm: null })}>
              <UsersPage />
            </FarmContext.Provider>
          </MeContext.Provider>
        </AuthContext.Provider>
      </MemoryRouter>
    );
    const view = render(tree(true));
    await screen.findByText("worker@farm.test");

    openCreate();
    selectAdminRole();
    fireEvent.change(ownerPasswordInput(), { target: { value: "still-typing" } });
    expect(ownerPasswordInput()).toHaveValue("still-typing");

    view.rerender(tree(false)); // simulated logout

    expect(ownerPasswordInput()).toHaveValue("");
  });
});

// #479 — one slot per PLACE a message can appear. This screen has five dialogs
// and, before the split, one string behind all of them: every open dialog
// rendered `{error && …}` unconditionally, so whichever failure happened last
// appeared inside every form on screen at once.
describe("UsersPage error placement (#479)", () => {
  const rowFor = (email: string) => screen.getByRole("row", { name: new RegExp(email) });
  const openRowDialog = (email: string, action: string) =>
    fireEvent.click(within(rowFor(email)).getByRole("button", { name: action }));

  it("shows a failed create inside the create dialog only", async () => {
    mockCreateUser.mockRejectedValue(new ApiError(409, "Conflict", "That email is already registered."));
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText(/Email/), { target: { value: "dup@farm.test" } });
    fireEvent.change(within(dialog()).getByLabelText(/^Password/), { target: { value: `Pw${Date.now()}!a` } });
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });

    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" }));
    });

    expect(within(dialog()).getByText("That email is already registered.")).toBeInTheDocument();
    expect(screen.getAllByText("That email is already registered.")).toHaveLength(1);
  });

  it("shows a failed rename inside the edit dialog only", async () => {
    mockUpdateUser.mockRejectedValue(new ApiError(422, "Validation failed", "That name is too long."));
    await renderReady(ADMIN);
    openRowDialog("worker@farm.test", "edit");

    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });

    expect(within(dialog()).getByText("That name is too long.")).toBeInTheDocument();
    expect(screen.getAllByText("That name is too long.")).toHaveLength(1);
  });

  it("shows a failed password reset inside the password dialog only", async () => {
    mockSetUserPassword.mockRejectedValue(new ApiError(422, "Validation failed", "That password is too weak."));
    await renderReady(ADMIN);
    openRowDialog("worker@farm.test", "password");
    const pw = `Pw${Date.now()}!a`;
    const fields = within(dialog()).getAllByLabelText(/password/i);
    fireEvent.change(fields[0], { target: { value: pw } });
    fireEvent.change(fields[1], { target: { value: pw } });
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });

    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Set password" }));
    });

    expect(within(dialog()).getByText("That password is too weak.")).toBeInTheDocument();
    expect(screen.getAllByText("That password is too weak.")).toHaveLength(1);
  });

  it("shows a mismatched password inside the password dialog, not on the page behind", async () => {
    // Client-side validation, reachable on every mistyped confirmation — not a
    // race. It is the dialog's own complaint about the dialog's own fields.
    await renderReady(ADMIN);
    openRowDialog("worker@farm.test", "password");
    const fields = within(dialog()).getAllByLabelText(/password/i);
    fireEvent.change(fields[0], { target: { value: `Pw${Date.now()}!a` } });
    fireEvent.change(fields[1], { target: { value: `Different${Date.now()}!b` } });
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });

    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Set password" }));
    });

    const mismatch = i18n.t("users:passwordMismatchMessage");
    expect(within(dialog()).getByText(mismatch)).toBeInTheDocument();
    expect(screen.getAllByText(mismatch)).toHaveLength(1);
    expect(mockSetUserPassword).not.toHaveBeenCalled();
  });

  it("shows a failed role change inside the role dialog only", async () => {
    mockChangeUserRole.mockRejectedValue(new ApiError(409, "Conflict", "That user is the last Owner."));
    await renderReady(ADMIN);
    openRowDialog("worker@farm.test", "role");
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });

    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Change role" }));
    });

    expect(within(dialog()).getByText("That user is the last Owner.")).toBeInTheDocument();
    expect(screen.getAllByText("That user is the last Owner.")).toHaveLength(1);
  });

  it("shows a failed flock assignment inside the flock dialog only", async () => {
    mockAssignFlock.mockRejectedValue(new ApiError(409, "Conflict", "That flock is already assigned."));
    await renderReady(ADMIN);
    await act(async () => {
      openRowDialog("worker@farm.test", "flocks");
    });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Assign flock" }));
    });

    expect(within(dialog()).getByText("That flock is already assigned.")).toBeInTheDocument();
    expect(screen.getAllByText("That flock is already assigned.")).toHaveLength(1);
  });

  // Displacement: each of these scopes is fixed across users, and a second
  // user's dialog can begin without the first being dismissed — the row
  // buttons behind the backdrop stay reachable to a screen reader's virtual
  // cursor (#480). Without an abandon on the user switch, user A's verdict
  // renders inside a dialog titled with user B's email (pi review of #491).
  it("does not carry one user's failed rename into another user's edit dialog", async () => {
    mockUpdateUser.mockRejectedValue(new ApiError(422, "Validation failed", "That name is too long."));
    await renderReady(ADMIN);
    openRowDialog("worker@farm.test", "edit");
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });
    expect(within(dialog()).getByText("That name is too long.")).toBeInTheDocument();

    openRowDialog("boss@farm.test", "edit");
    // The dialog really swapped users — its title names the new email.
    expect(dialog()).toHaveAccessibleName(/boss@farm\.test/);
    expect(screen.queryByText("That name is too long.")).not.toBeInTheDocument();
  });

  it("does not carry one user's failed password reset into another user's dialog", async () => {
    mockSetUserPassword.mockRejectedValue(new ApiError(422, "Validation failed", "That password is too weak."));
    await renderReady(ADMIN);
    openRowDialog("worker@farm.test", "password");
    const pw = `Pw${Date.now()}!a`;
    const fields = within(dialog()).getAllByLabelText(/password/i);
    fireEvent.change(fields[0], { target: { value: pw } });
    fireEvent.change(fields[1], { target: { value: pw } });
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Set password" }));
    });
    expect(within(dialog()).getByText("That password is too weak.")).toBeInTheDocument();

    openRowDialog("boss@farm.test", "password");
    expect(dialog()).toHaveAccessibleName(/boss@farm\.test/);
    expect(screen.queryByText("That password is too weak.")).not.toBeInTheDocument();
  });

  it("does not carry one user's failed role change into another user's dialog", async () => {
    mockChangeUserRole.mockRejectedValue(new ApiError(409, "Conflict", "That user is the last Owner."));
    await renderReady(ADMIN);
    openRowDialog("worker@farm.test", "role");
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Change role" }));
    });
    expect(within(dialog()).getByText("That user is the last Owner.")).toBeInTheDocument();

    openRowDialog("boss@farm.test", "role");
    expect(dialog()).toHaveAccessibleName(/boss@farm\.test/);
    expect(screen.queryByText("That user is the last Owner.")).not.toBeInTheDocument();
  });

  it("does not carry one worker's failed assignment into another worker's flock dialog", async () => {
    const WORKER_2: User = { id: "u-w2", email: "second@farm.test", displayName: null, role: "Worker", disabledAt: null };
    mockListUsers.mockResolvedValue([WORKER_USER, WORKER_2, ADMIN_USER]);
    mockAssignFlock.mockRejectedValue(new ApiError(409, "Conflict", "That flock is already assigned."));
    await renderReady(ADMIN);
    await act(async () => {
      openRowDialog("worker@farm.test", "flocks");
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Assign flock" }));
    });
    expect(within(dialog()).getByText("That flock is already assigned.")).toBeInTheDocument();

    await act(async () => {
      openRowDialog("second@farm.test", "flocks");
    });
    expect(dialog()).toHaveAccessibleName(/second@farm\.test/);
    expect(screen.queryByText("That flock is already assigned.")).not.toBeInTheDocument();
  });

  // The load runs BEFORE the dialog rebinds — a failed load never opens the
  // second worker's dialog (see the comment on `openAssignments`), so it
  // must not abandon the first worker's still-open one. Abandoning up front
  // would erase worker A's visible message while A's dialog stays open and
  // unchanged (adversarial review of #491).
  it("keeps worker A's dialog and its message when worker B's load fails", async () => {
    const WORKER_2: User = { id: "u-w2", email: "second@farm.test", displayName: null, role: "Worker", disabledAt: null };
    mockListUsers.mockResolvedValue([WORKER_USER, WORKER_2, ADMIN_USER]);
    mockAssignFlock.mockRejectedValue(new ApiError(409, "Conflict", "That flock is already assigned."));
    await renderReady(ADMIN);
    await act(async () => {
      openRowDialog("worker@farm.test", "flocks");
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Assign flock" }));
    });
    expect(within(dialog()).getByText("That flock is already assigned.")).toBeInTheDocument();

    mockListAssignments.mockRejectedValueOnce(new ApiError(500, "Server error", "Could not load flock access."));
    await act(async () => {
      openRowDialog("second@farm.test", "flocks");
    });

    // Still worker A's dialog — B's never opened.
    expect(dialog()).toHaveAccessibleName(/worker@farm\.test/);
    expect(within(dialog()).getByText("That flock is already assigned.")).toBeInTheDocument();
  });

  it("keeps one dialog's failure out of another dialog opened beside it", async () => {
    // Nothing on this screen enforces one-open-dialog: `editUser` and `pwUser`
    // are independent state and both row buttons stay live. With one shared
    // slot the rename's failure rendered inside the password form too — the
    // #480 finding, on the screen that has five of them.
    mockUpdateUser.mockRejectedValue(new ApiError(422, "Validation failed", "That name is too long."));
    await renderReady(ADMIN);
    openRowDialog("worker@farm.test", "edit");
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Save" }));
    });

    openRowDialog("worker@farm.test", "password");

    const dialogs = screen.getAllByRole("dialog");
    const password = dialogs.find((d) => within(d).queryByRole("button", { name: "Set password" }))!;
    expect(within(password).queryByText("That name is too long.")).not.toBeInTheDocument();
    expect(screen.getAllByText("That name is too long.")).toHaveLength(1);
  });

  it("keeps a page failure while a dialog opens and its own write fails", async () => {
    // The flock-assignment READ fails before its dialog can open, so its
    // message is the screen's. Opening an unrelated form must not swallow it,
    // and that form's own failure must not replace it.
    mockListAssignments.mockRejectedValue(new ApiError(500, "Server error", "Could not load flock access."));
    mockCreateUser.mockRejectedValue(new ApiError(409, "Conflict", "That email is already registered."));
    await renderReady(ADMIN);
    await act(async () => {
      openRowDialog("worker@farm.test", "flocks");
    });
    expect(screen.getByText("Could not load flock access.")).toBeInTheDocument();

    openCreate();
    expect(screen.getByText("Could not load flock access.")).toBeInTheDocument();

    fireEvent.change(within(dialog()).getByLabelText(/Email/), { target: { value: "dup@farm.test" } });
    fireEvent.change(within(dialog()).getByLabelText(/^Password/), { target: { value: `Pw${Date.now()}!a` } });
    fireEvent.change(within(dialog()).getByLabelText(/Your current password/), {
      target: { value: OWNER_STEP_UP_PASSWORD },
    });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" }));
    });

    expect(within(dialog()).getByText("That email is already registered.")).toBeInTheDocument();
    expect(screen.getByText("Could not load flock access.")).toBeInTheDocument();
  });
});
