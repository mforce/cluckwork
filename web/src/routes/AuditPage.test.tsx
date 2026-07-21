import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, within, act, fireEvent } from "@testing-library/react";
import { AuditPage } from "./AuditPage";
import { listAuditEvents } from "../api/cluckwork";
import type { AuditEvent } from "../api/cluckwork";

// AuditPage's only network dep is listAuditEvents; mock that seam so the screen
// renders against controlled data — no network, no backend. ApiError stays real
// (from ../api/client, unmocked) so errText's instanceof checks still hold.
//
// Role gating is intentionally NOT tested: AuditPage reads neither useAuth nor
// the router — it renders identically for any session. The #93 comment is
// explicit that the admin-only guarantee is enforced by the route/API, not the
// component, so there is no in-component gating behavior to assert. That is why
// a plain `render` (no AuthProvider / MemoryRouter) is sufficient here.
vi.mock("../api/cluckwork", () => ({
  listAuditEvents: vi.fn(),
}));

const mockListAuditEvents = vi.mocked(listAuditEvents);

const EVENT_A: AuditEvent = {
  id: "a1",
  occurredAtUtc: "2026-07-19T14:30:05.123Z",
  actorEmail: "admin@farm.test",
  action: "Flock.Deplete",
  entityType: "Flock",
  entityId: "f1234567-89ab-cdef",
  reason: "culled sick birds",
  detailsJson: '{"count":5}',
};
const EVENT_B: AuditEvent = {
  id: "a2",
  occurredAtUtc: "2026-07-18T09:15:00Z",
  actorEmail: "manager@farm.test",
  action: "User.Create",
  entityType: "User",
  entityId: "u9abcdef-0000",
  reason: null,
  detailsJson: null,
};

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  mockListAuditEvents.mockResolvedValue([]); // default mount-load: empty page
});

describe("AuditPage load + render", () => {
  it("shows a loading state until the first audit page resolves", async () => {
    let resolve!: (events: AuditEvent[]) => void;
    mockListAuditEvents.mockReturnValue(new Promise<AuditEvent[]>((r) => (resolve = r)));
    render(<AuditPage />);

    expect(screen.getByText(/Loading/)).toBeInTheDocument();
    resolve([]); // settle so the pending fetch doesn't dangle past the test
    await screen.findByText("No audit events yet.");
  });

  // The error branch (listAuditEvents rejects → setError) is NOT asserted: it
  // lives in the mount effect, and in this Vitest 3 + React 19 stack a rejection
  // the component *does* handle is still flagged as an unhandled rejection
  // (documented false positive, matching the StockPage exemplar's rationale).

  it("shows the empty-state hint when there are no audit events", async () => {
    mockListAuditEvents.mockResolvedValue([]);
    render(<AuditPage />);
    expect(await screen.findByText("No audit events yet.")).toBeInTheDocument();
  });

  it("loads the first page on mount with no action filter, limit 100, offset 0", async () => {
    mockListAuditEvents.mockResolvedValue([]);
    render(<AuditPage />);
    await screen.findByText("No audit events yet.");
    expect(mockListAuditEvents).toHaveBeenCalledWith(
      expect.objectContaining({ action: undefined, limit: 100, offset: 0 }),
    );
  });

  it("maps each audit event's actor, action, entity and reason into its row", async () => {
    mockListAuditEvents.mockResolvedValue([EVENT_A, EVENT_B]);
    render(<AuditPage />);

    const rowA = await screen.findByRole("row", { name: /admin@farm\.test/ });
    // occurredAtUtc: "T" → space, truncated to the first 19 chars (drops ms/Z).
    expect(within(rowA).getByText("2026-07-19 14:30:05")).toBeInTheDocument();
    expect(within(rowA).getByText("Flock.Deplete")).toBeInTheDocument();
    // entityType + first 8 chars of entityId.
    expect(within(rowA).getByText("Flock f1234567")).toBeInTheDocument();
    expect(within(rowA).getByText("culled sick birds")).toBeInTheDocument();

    const rowB = screen.getByRole("row", { name: /manager@farm\.test/ });
    expect(within(rowB).getByText("User u9abcdef")).toBeInTheDocument();
    expect(within(rowB).getByText("—")).toBeInTheDocument(); // null reason → em dash
  });

  it("renders a read-only view with no mutation controls (and no paging on a short page)", async () => {
    mockListAuditEvents.mockResolvedValue([EVENT_A, EVENT_B]); // 2 < PAGE ⇒ no 'load more'
    render(<AuditPage />);
    await screen.findByRole("row", { name: /admin@farm\.test/ });
    // #93: the audit trail is deliberately read-only — no adjust/void/delete
    // controls — and 'load more' only appears when a full page came back.
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });
});

describe("AuditPage filter", () => {
  it("re-queries listAuditEvents with the chosen action when the filter changes", async () => {
    mockListAuditEvents.mockResolvedValue([]);
    render(<AuditPage />);
    await screen.findByText("No audit events yet."); // let the mount load settle first

    await act(async () => {
      fireEvent.change(screen.getByRole("combobox"), { target: { value: "Flock.Deplete" } });
    });

    // The ARGUMENT is the behavior: the chosen action must reach the seam.
    expect(mockListAuditEvents).toHaveBeenLastCalledWith(
      expect.objectContaining({ action: "Flock.Deplete", offset: 0 }),
    );
  });
});

describe("AuditPage paging", () => {
  it("passes the current row count as the offset when 'load more' is clicked", async () => {
    // A full page (=== PAGE 100) is what sets hasMore, exposing 'load more'.
    const firstPage: AuditEvent[] = Array.from({ length: 100 }, (_, i) => ({
      ...EVENT_A, id: `p1-${i}`, actorEmail: `user${i}@farm.test`,
    }));
    const secondPage: AuditEvent[] = [{ ...EVENT_B, id: "p2-0", actorEmail: "next@farm.test" }];
    mockListAuditEvents.mockResolvedValueOnce(firstPage).mockResolvedValueOnce(secondPage);
    render(<AuditPage />);

    const loadMore = await screen.findByRole("button", { name: "load more" });
    await act(async () => {
      fireEvent.click(loadMore);
    });

    expect(mockListAuditEvents).toHaveBeenLastCalledWith(
      expect.objectContaining({ offset: 100 }),
    );
    expect(await screen.findByText("next@farm.test")).toBeInTheDocument(); // appended page row
  });
});
