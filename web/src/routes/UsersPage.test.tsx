import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within, fireEvent, act } from "@testing-library/react";
import { UsersPage } from "./UsersPage";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  assignFlock, createUser, listFlockAssignments, listFlocks, listUsers, unassignFlock,
} from "../api/cluckwork";
import type { Flock, FlockAssignment, User } from "../api/cluckwork";
import { ApiError } from "../api/client";

// Network seam only; ApiError stays real (errText branches on `instanceof`).
vi.mock("../api/cluckwork", () => ({
  listUsers: vi.fn(),
  createUser: vi.fn(),
  listFlockAssignments: vi.fn(),
  assignFlock: vi.fn(),
  unassignFlock: vi.fn(),
  listFlocks: vi.fn(),
}));

const mockListUsers = vi.mocked(listUsers);
const mockCreateUser = vi.mocked(createUser);
const mockListAssignments = vi.mocked(listFlockAssignments);
const mockAssignFlock = vi.mocked(assignFlock);
const mockUnassignFlock = vi.mocked(unassignFlock);
const mockListFlocks = vi.mocked(listFlocks);

const WORKER_USER: User = { id: "u-w", email: "worker@farm.test", displayName: "Wendy", role: "Worker" };
const ADMIN_USER: User = { id: "u-a", email: "boss@farm.test", displayName: null, role: "Admin" };

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

describe("UsersPage dialog dismissal", () => {
  it("closes the create dialog on Cancel without writing", async () => {
    await renderReady(ADMIN);
    openCreate();
    fireEvent.change(within(dialog()).getByLabelText("Email *"), { target: { value: "nope@farm.test" } });

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(mockCreateUser).not.toHaveBeenCalled();
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
    expect(await screen.findByText("Assigned flocks")).toBeInTheDocument();
    // The assignment's flock id resolves to a name via the loaded flocks list.
    // Scope to the <li> so it doesn't collide with the dropdown's "Coop A" option.
    const item = screen.getByRole("listitem");
    expect(within(item).getByText("Coop A")).toBeInTheDocument();
    expect(within(item).getByRole("button", { name: "remove" })).toBeInTheDocument();
    // Toggle flips to "hide flocks".
    expect(within(workerRow).getByRole("button", { name: "hide flocks" })).toBeInTheDocument();
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

  it("collapses the panel again on 'hide flocks'", async () => {
    mockListAssignments.mockResolvedValue([ASSIGN_1]);
    await renderReady(ADMIN);

    const workerRow = () => screen.getByRole("row", { name: /worker@farm.test/ });
    await act(async () => {
      fireEvent.click(within(workerRow()).getByRole("button", { name: "flocks" }));
    });
    expect(await screen.findByText("Assigned flocks")).toBeInTheDocument();

    fireEvent.click(within(workerRow()).getByRole("button", { name: "hide flocks" }));
    expect(screen.queryByText("Assigned flocks")).not.toBeInTheDocument();
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
    expect(screen.getByText("Assigned flocks")).toBeInTheDocument(); // panel stayed open
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
