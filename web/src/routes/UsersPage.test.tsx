import { useState } from "react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act, render } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { UsersPage } from "./UsersPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { farmState } from "../test/fixtures";
import { AuthContext } from "../auth/AuthContext";
import type { Role } from "../auth/claims";
import { FarmContext } from "../farm/FarmContext";
import { MeContext } from "../session/SessionContext";
import type { Me } from "../api/cluckwork";
import {
  assignFlock, changeUserRole, createUser, listFlockAssignments, listFlocks, listUsers,
  setUserPassword, unassignFlock, updateUser,
} from "../api/cluckwork";
import type { Flock, FlockAssignment, User } from "../api/cluckwork";
import { ApiError, stepUp } from "../api/client";
import i18n from "../i18n";

// Network seam only; ApiError stays real (errText branches on `instanceof`).
vi.mock("../api/cluckwork", () => ({
  listUsers: vi.fn(),
  createUser: vi.fn(),
  updateUser: vi.fn(),
  setUserPassword: vi.fn(),
  changeUserRole: vi.fn(),
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
const mockListAssignments = vi.mocked(listFlockAssignments);
const mockAssignFlock = vi.mocked(assignFlock);
const mockUnassignFlock = vi.mocked(unassignFlock);
const mockListFlocks = vi.mocked(listFlocks);
const mockStepUp = vi.mocked(stepUp);

const WORKER_USER: User = { id: "u-w", email: "worker@farm.test", displayName: "Wendy", role: "Worker" };
const ADMIN_USER: User = { id: "u-a", email: "boss@farm.test", displayName: null, role: "Admin" };
// Role wiring fixture (#182, Task 22): ReadOnly is the one role whose enum
// label is NOT its raw wire value (enums:role.ReadOnly = "Read-only"), so it's
// the fixture that actually distinguishes roleLabel(u.role) from a plain
// {u.role} render.
const READONLY_USER: User = { id: "u-r", email: "ro@farm.test", displayName: null, role: "ReadOnly" };

const flock = (id: string, name: string, status = "Active"): Flock => ({
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
    fireEvent.change(within(dialog()).getByLabelText(/Password/), { target: { value: password } });
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Manager" } }); // off the "Worker" default
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" }));
    });

    const body = mockCreateUser.mock.calls[0][0];
    expect(body).toMatchObject({ email: "New@Farm.test", role: "Manager" }); // email trimmed, role chosen
    // Pin that the exact typed password reaches the request body (not a shape check).
    expect(body.password).toBe(password);
    expect(mockCreateUser.mock.calls[0][1]).toEqual(expect.any(String)); // idempotency key

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
    fireEvent.change(within(dialog()).getByLabelText(/Password/), { target: { value: `pw-${crypto.randomUUID()}` } });
    fireEvent.change(within(dialog()).getByLabelText("Name"), { target: { value: "  Ada Lovelace  " } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" })); });
    expect(mockCreateUser.mock.calls[0][0]).toMatchObject({ email: "named@farm.test", name: "Ada Lovelace" });

    // Without a name → the field is omitted (undefined), not sent blank.
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "anon@farm.test" } });
    fireEvent.change(within(dialog()).getByLabelText(/Password/), { target: { value: `pw-${crypto.randomUUID()}` } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" })); });
    expect(mockCreateUser.mock.calls[1][0].name).toBeUndefined();
  });

  it("replays the SAME create key after a failure, and rotates it after success", async () => {
    mockCreateUser.mockRejectedValueOnce(new ApiError(500, "Server error", "boom"));
    mockCreateUser.mockResolvedValue({ id: "u-new" });
    await renderReady(ADMIN);

    openCreate();
    const emailInput = () => within(dialog()).getByLabelText("Email *");
    const pwInput = () => within(dialog()).getByLabelText(/Password/);
    const submit = () => within(dialog()).getByRole("button", { name: "Create user" });

    // Attempt 1 — same email → same scope; fails, so the key is kept.
    fireEvent.change(emailInput(), { target: { value: "one@farm.test" } });
    fireEvent.change(pwInput(), { target: { value: `pw-${crypto.randomUUID()}` } });
    await act(async () => { fireEvent.click(submit()); });
    // A failure keeps the dialog up with the error inside it.
    expect(within(dialog()).getByText(/Server error|boom/)).toBeInTheDocument();

    // Attempt 2 — email/password survive a failure; resubmit as-is → replay.
    await act(async () => { fireEvent.click(submit()); });

    // Attempt 3 — success closed the dialog and reset the form, so reopen and
    // refill the same email → fresh key.
    openCreate();
    fireEvent.change(emailInput(), { target: { value: "one@farm.test" } });
    fireEvent.change(pwInput(), { target: { value: `pw-${crypto.randomUUID()}` } });
    await act(async () => { fireEvent.click(submit()); });

    const k1 = mockCreateUser.mock.calls[0][1];
    const k2 = mockCreateUser.mock.calls[1][1];
    const k3 = mockCreateUser.mock.calls[2][1];
    expect(k2).toBe(k1); // failure kept the key → exact replay
    expect(k3).not.toBe(k2); // success rotated it → next write is fresh
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
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Set password" })); });

    // #308 — the 4th arg (stepUpToken) is undefined: the target is a Worker,
    // not an Owner, so no step-up grant is requested at all.
    expect(mockSetUserPassword).toHaveBeenCalledWith(
      "u-w", { newPassword: password }, expect.any(String), undefined);
    // Success closes the dialog and says the target was signed out.
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(await screen.findByText(/signed out everywhere/i)).toBeInTheDocument();
  });

  it("refuses a mismatched confirmation without calling the server", async () => {
    await renderReady(ADMIN);

    openPw(/worker@farm.test/);
    fireEvent.change(within(dialog()).getByLabelText(/New password/), { target: { value: freshPassword() } });
    fireEvent.change(within(dialog()).getByLabelText(/Confirm new password/), { target: { value: freshPassword() } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Set password" })); });

    expect(mockSetUserPassword).not.toHaveBeenCalled();
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
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Change role" }));
    });

    // #308 — the 4th arg (stepUpToken) is undefined: the requested role is
    // Manager, not Owner, so no step-up grant is requested at all.
    expect(mockChangeUserRole).toHaveBeenCalledWith(
      "u-w", { role: "Manager" }, expect.any(String), undefined);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(await screen.findByText(/worker@farm\.test is now Manager/)).toBeInTheDocument();
    expect(mockListUsers).toHaveBeenCalledTimes(2); // initial load + post-change refresh
  });

  it("keeps the dialog open and shows the error when the server rejects it", async () => {
    mockChangeUserRole.mockRejectedValue(new ApiError(422, "Users.LastOwner", "cannot demote the sole remaining owner"));
    await renderReady(ADMIN);

    openRole(/boss@farm.test/);
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Manager" } });
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

describe("UsersPage change-role step-up (#308, #355)", () => {
  const openRole = (rowName: RegExp) =>
    fireEvent.click(within(screen.getByRole("row", { name: rowName })).getByRole("button", { name: "role" }));
  const ownerPasswordInput = () => within(dialog()).getByLabelText(/Your current password/);
  const selectAdminRole = () =>
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Admin" } });

  it("shows the step-up field only once Admin (Owner) is picked as the requested role", async () => {
    await renderReady(ADMIN);
    openRole(/worker@farm.test/);

    expect(within(dialog()).queryByLabelText(/Your current password/)).not.toBeInTheDocument();
    selectAdminRole();
    expect(ownerPasswordInput()).toBeInTheDocument();
  });

  it("does not prompt at all when demoting an existing Owner (the requested role isn't Owner)", async () => {
    await renderReady(ADMIN);
    openRole(/boss@farm.test/);
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Manager" } });
    expect(within(dialog()).queryByLabelText(/Your current password/)).not.toBeInTheDocument();
  });

  it("promoting to Owner exchanges the current password for a grant and attaches it", async () => {
    mockStepUp.mockResolvedValue({ token: "grant-789", expiresAt: "2026-01-01T00:05:00Z" });
    mockChangeUserRole.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    openRole(/worker@farm.test/);
    selectAdminRole();
    fireEvent.change(ownerPasswordInput(), { target: { value: "OwnerCurrentPw!1" } });
    await act(async () => {
      fireEvent.click(within(dialog()).getByRole("button", { name: "Change role" }));
    });

    expect(mockStepUp).toHaveBeenCalledWith("OwnerCurrentPw!1");
    expect(mockChangeUserRole).toHaveBeenCalledWith(
      "u-w", { role: "Admin" }, expect.any(String), "grant-789");
  });

  it("never stores the entered step-up password: reopening after a successful use shows it empty", async () => {
    mockStepUp.mockResolvedValue({ token: "grant-789", expiresAt: "2026-01-01T00:05:00Z" });
    mockChangeUserRole.mockResolvedValue(undefined);
    await renderReady(ADMIN);

    openRole(/worker@farm.test/);
    selectAdminRole();
    fireEvent.change(ownerPasswordInput(), { target: { value: "OwnerCurrentPw!1" } });
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
          isAuthenticated, isLoading: false, isAdmin: true, role: "Admin" as Role,
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
    const WORKER_2: User = { id: "u-w2", email: "worker2@farm.test", displayName: "Walt", role: "Worker" };
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

// #308 — step-up re-confirmation for the two sensitive user-administration
// actions (creating another Owner; resetting an Owner's password). Every
// other role/target combination is proven UNCHANGED by the existing "create"
// and "set password" describe blocks above (they never fill/expect a
// step-up field and still pass) — that is the "no blanket prompt" half of
// the acceptance criteria; this block covers the gated half plus the SPA's
// own contract (prompt only when needed, never store the password, clear on
// logout).
describe("UsersPage step-up authentication (#308)", () => {
  const ownerPasswordInput = () => within(dialog()).getByLabelText(/Your current password/);
  const createPasswordInput = () => within(dialog()).getByLabelText(/Password \(min 12 chars\)/);
  const selectAdminRole = () =>
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Admin" } });
  const openPwFor = (rowName: RegExp) =>
    fireEvent.click(within(screen.getByRole("row", { name: rowName })).getByRole("button", { name: "password" }));

  it("shows the step-up field only once the Admin (Owner) role is picked, never for any other role", async () => {
    await renderReady(ADMIN);
    openCreate();

    expect(within(dialog()).queryByLabelText(/Your current password/)).not.toBeInTheDocument();
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Manager" } });
    expect(within(dialog()).queryByLabelText(/Your current password/)).not.toBeInTheDocument();

    selectAdminRole();
    expect(ownerPasswordInput()).toBeInTheDocument();
  });

  it("success: creating another Owner exchanges the current password for a grant and attaches it", async () => {
    mockStepUp.mockResolvedValue({ token: "grant-123", expiresAt: "2026-01-01T00:05:00Z" });
    mockCreateUser.mockResolvedValue({ id: "u-new" });
    await renderReady(ADMIN);
    openCreate();

    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "boss@farm.test" } });
    fireEvent.change(createPasswordInput(), { target: { value: `pw-${crypto.randomUUID()}` } });
    selectAdminRole();
    fireEvent.change(ownerPasswordInput(), { target: { value: "OwnerCurrentPw!1" } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" })); });

    expect(mockStepUp).toHaveBeenCalledWith("OwnerCurrentPw!1");
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
    fireEvent.change(ownerPasswordInput(), { target: { value: "OwnerCurrentPw!1" } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" })); });

    openCreate();
    selectAdminRole();
    expect(ownerPasswordInput()).toHaveValue("");
  });

  // #336 review — the leak the test above cannot see. Its Owner submit runs the
  // `role === OWNER_ROLE` branch, which clears the field on the way past. Switch
  // BACK to a non-Owner role after typing and that branch never runs, so only
  // the dialog-close reset can clear it — and the success path used to repeat
  // the field resets inline instead of calling closeCreate(), missing this one.
  // The operator's OWN account password then survived into the next open. Same
  // shape as #314: a second reset path that drifted from the first.
  it("clears the step-up password when the role is switched away from Owner before a successful create", async () => {
    mockCreateUser.mockResolvedValue({ id: "u-new" });
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "worker@farm.test" } });
    fireEvent.change(createPasswordInput(), { target: { value: `pw-${crypto.randomUUID()}` } });

    // Pick Owner, type the proof password, then change your mind.
    selectAdminRole();
    fireEvent.change(ownerPasswordInput(), { target: { value: "OwnerCurrentPw!1" } });
    fireEvent.change(within(dialog()).getByLabelText("Role"), { target: { value: "Worker" } });

    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" })); });

    // No grant was needed for a Worker — so nothing cleared the field en route.
    expect(mockStepUp).not.toHaveBeenCalled();
    expect(mockCreateUser.mock.calls[0][2]).toBeUndefined();

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
    fireEvent.change(ownerPasswordInput(), { target: { value: "OwnerCurrentPw!1" } });

    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Create user" })); });

    expect(within(dialog()).getByText(/re-authentication is required/i)).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("does not prompt at all when resetting a non-Owner's password", async () => {
    await renderReady(ADMIN);
    openPwFor(/worker@farm.test/);
    expect(screen.queryByLabelText(/Your current password/)).not.toBeInTheDocument();
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
    fireEvent.change(ownerPasswordInput(), { target: { value: "OwnerCurrentPw!1" } });
    await act(async () => { fireEvent.click(within(dialog()).getByRole("button", { name: "Set password" })); });

    expect(mockStepUp).toHaveBeenCalledWith("OwnerCurrentPw!1");
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
          isAuthenticated, isLoading: false, isAdmin: true, role: "Admin" as Role,
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
