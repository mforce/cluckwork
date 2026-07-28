import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, within, act, fireEvent } from "@testing-library/react";
import { AuditPage } from "./AuditPage";
import { listAuditEvents } from "../api/cluckwork";
import type { AuditEvent } from "../api/cluckwork";
import i18n from "../i18n";

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

  // #182, Task 29 — the filter option TEXT is now auditActionLabel(a) (the
  // friendly label), not the raw code; the option's VALUE (the server filter
  // param) stays the raw code. Asserted as two separate expectations rather
  // than just getByRole({name}) so a regression that renders the raw code as
  // the option's visible text (silently breaking the friendly-label feature
  // while still passing a value-only check) fails loudly here.
  it("offers User.Update as a filterable action, labelled and value-preserved (#163 — the list must be exhaustive)", async () => {
    render(<AuditPage />);
    await screen.findByText("No audit events yet.");
    const option = screen.getByRole("option", { name: "User updated" }) as HTMLOptionElement;
    expect(option.value).toBe("User.Update");
    expect(screen.queryByRole("option", { name: "User.Update" })).not.toBeInTheDocument();
  });

  it("offers both password actions as filterable, labelled and value-preserved (#165)", async () => {
    render(<AuditPage />);
    await screen.findByText("No audit events yet.");
    const setOption = screen.getByRole("option", { name: "Password set" }) as HTMLOptionElement;
    const changedOption = screen.getByRole("option", { name: "Password changed" }) as HTMLOptionElement;
    expect(setOption.value).toBe("User.PasswordSet");
    expect(changedOption.value).toBe("User.PasswordChanged");
  });

  // #247 — the server emits Account.SetLogo/RemoveLogo/UpdateSettings, but the
  // client filter list was missing all three, so admins could not filter by
  // them (rows only showed under "All actions"). They must now be offered,
  // labelled + value-preserved like every other action.
  it("offers the farm logo + settings actions as filterable, labelled and value-preserved (#247)", async () => {
    render(<AuditPage />);
    await screen.findByText("No audit events yet.");
    const setLogo = screen.getByRole("option", { name: "Farm logo set" }) as HTMLOptionElement;
    const removeLogo = screen.getByRole("option", { name: "Farm logo removed" }) as HTMLOptionElement;
    const updateSettings = screen.getByRole("option", { name: "Farm settings updated" }) as HTMLOptionElement;
    expect(setLogo.value).toBe("Account.SetLogo");
    expect(removeLogo.value).toBe("Account.RemoveLogo");
    expect(updateSettings.value).toBe("Account.UpdateSettings");
    // The raw codes must not leak as the visible option text.
    expect(screen.queryByRole("option", { name: "Account.SetLogo" })).not.toBeInTheDocument();
  });

  // #247 — a logo row carries entityType "FarmLogo", which was absent from
  // ENTITY_TYPE_VALUES and so degraded to the raw "FarmLogo" string. It must
  // now render the friendly, translatable label through entityTypeLabel().
  it("renders the FarmLogo entity type with its friendly label, not the raw code (#247)", async () => {
    const LOGO_EVENT: AuditEvent = {
      id: "a3",
      occurredAtUtc: "2026-07-20T10:00:00Z",
      actorEmail: "admin@farm.test",
      action: "Account.SetLogo",
      entityType: "FarmLogo",
      entityId: "fl123456-89ab-cdef",
      reason: null,
      detailsJson: null,
    };
    mockListAuditEvents.mockResolvedValue([LOGO_EVENT]);
    render(<AuditPage />);

    const row = await screen.findByRole("row", { name: /admin@farm\.test/ });
    // Action cell: friendly label, not the raw "Account.SetLogo".
    expect(within(row).getByText("Farm logo set")).toBeInTheDocument();
    // Entity cell: entityTypeLabel("FarmLogo") + first 8 chars of the id.
    expect(within(row).getByText("Farm logo fl123456")).toBeInTheDocument();
    expect(within(row).queryByText("FarmLogo fl123456")).not.toBeInTheDocument();
  });

  it("maps each audit event's actor, action, entity and reason into its row", async () => {
    mockListAuditEvents.mockResolvedValue([EVENT_A, EVENT_B]);
    render(<AuditPage />);

    const rowA = await screen.findByRole("row", { name: /admin@farm\.test/ });
    // occurredAtUtc: "T" → space, truncated to the first 19 chars (drops ms/Z).
    expect(within(rowA).getByText("2026-07-19 14:30:05")).toBeInTheDocument();
    // Action cell renders auditActionLabel(e.action), not the raw code.
    expect(within(rowA).getByText("Flock depleted")).toBeInTheDocument();
    expect(within(rowA).queryByText("Flock.Deplete")).not.toBeInTheDocument();
    // entityTypeLabel(entityType) + first 8 chars of entityId. "Flock" is an
    // identity label (enums:entityType.Flock === "Flock"), so this also
    // covers the entity cell reading through entityTypeLabel.
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

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 29, batch B5)
// ---------------------------------------------------------------------------

// `audit` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting it, even under a non-English locale, would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at runtime
// instead, the same i18n.addResource technique the other batches use
// (see e.g. UsersPage.test.tsx), so each marker only renders if the screen
// actually reads the catalog rather than a literal that happens to still
// match it. `enums` IS translated, but the override technique is the same:
// it proves the SITE reads the catalog through auditActionLabel/
// entityTypeLabel, independent of which language is active.
describe("AuditPage i18n wiring (#182, Task 29)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the heading from the audit catalog, not a hardcoded literal", async () => {
    await withOverride("audit", "heading", "HEADING-MARKER", async () => {
      render(<AuditPage />);
      await screen.findByText("No audit events yet."); // let the mount load settle
      expect(screen.getByRole("heading", { name: "HEADING-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Audit log" })).not.toBeInTheDocument();
    });
  });

  it("reads the intro prose from the audit catalog, not a hardcoded literal", async () => {
    await withOverride("audit", "intro", "INTRO-MARKER", async () => {
      render(<AuditPage />);
      await screen.findByText("No audit events yet.");
      expect(screen.getByText("INTRO-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/Every corrective, destructive/)).not.toBeInTheDocument();
    });
  });

  it("reads the action filter label from the audit catalog, not a hardcoded literal", async () => {
    await withOverride("audit", "actionFilterLabel", "FILTER-LABEL-MARKER", async () => {
      render(<AuditPage />);
      await screen.findByText("No audit events yet.");
      // `<label>{t(...)}<select>…</select></label>` — getByLabelText matches
      // the label's own text only (excluding the wrapped select's rendered
      // option text), same pattern as UsersPage's role picker.
      expect(screen.getByLabelText("FILTER-LABEL-MARKER")).toBeInTheDocument();
    });
  });

  it("reads the 'All actions' option from the audit catalog, not a hardcoded literal", async () => {
    await withOverride("audit", "allActionsOption", "ALL-ACTIONS-MARKER", async () => {
      render(<AuditPage />);
      await screen.findByText("No audit events yet.");
      expect(screen.getByRole("option", { name: "ALL-ACTIONS-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("option", { name: "All actions" })).not.toBeInTheDocument();
    });
  });

  it("reads every table header from the audit catalog, not a hardcoded literal", async () => {
    // At least one event, so the table (with its <thead>) actually renders —
    // the empty-state branch renders no headers at all.
    mockListAuditEvents.mockResolvedValue([EVENT_A]);
    // Headers are checked one at a time (each override restored before the
    // next) so a single shared render can't mask one key silently falling
    // back to English while another is overridden.
    for (const [key, marker, original] of [
      ["whenHeader", "WHEN-MARKER", "When (UTC)"],
      ["whoHeader", "WHO-MARKER", "Who"],
      ["actionHeader", "ACTION-HEADER-MARKER", "Action"],
      ["entityHeader", "ENTITY-MARKER", "Entity"],
      ["reasonHeader", "REASON-MARKER", "Reason"],
    ] as const) {
      await withOverride("audit", key, marker, async () => {
        // Unmounted at the end of this iteration (afterEach's cleanup() only
        // runs between `it()` blocks, not between loop iterations) — without
        // it, a prior iteration's un-overridden headers would stay in the
        // DOM and falsely satisfy/defeat the next iteration's queries.
        const { unmount } = render(<AuditPage />);
        await screen.findByRole("row", { name: /admin@farm\.test/ });
        expect(screen.getByRole("columnheader", { name: marker })).toBeInTheDocument();
        expect(screen.queryByRole("columnheader", { name: original })).not.toBeInTheDocument();
        unmount();
      });
    }
  });

  it("reads the empty-state message from the audit catalog, not a hardcoded literal", async () => {
    await withOverride("audit", "emptyMessage", "EMPTY-MARKER", async () => {
      render(<AuditPage />);
      expect(await screen.findByText("EMPTY-MARKER")).toBeInTheDocument();
      expect(screen.queryByText("No audit events yet.")).not.toBeInTheDocument();
    });
  });

  it("reads the 'load more' button from the audit catalog, not a hardcoded literal", async () => {
    const firstPage: AuditEvent[] = Array.from({ length: 100 }, (_, i) => ({
      ...EVENT_A, id: `lm-${i}`, actorEmail: `user${i}@farm.test`,
    }));
    mockListAuditEvents.mockResolvedValue(firstPage);
    await withOverride("audit", "loadMoreButton", "LOAD-MORE-MARKER", async () => {
      render(<AuditPage />);
      expect(await screen.findByRole("button", { name: "LOAD-MORE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "load more" })).not.toBeInTheDocument();
    });
  });

  // Proves the action table cell reads enums:auditAction.* through
  // auditActionLabel(e.action) rather than rendering e.action raw.
  it("reads the action cell from the enums catalog via auditActionLabel, not the raw wire code", async () => {
    mockListAuditEvents.mockResolvedValue([EVENT_A]); // action: "Flock.Deplete"
    await withOverride("enums", "auditAction.Flock.Deplete", "ACTION-LABEL-MARKER", async () => {
      render(<AuditPage />);
      const row = await screen.findByRole("row", { name: /admin@farm\.test/ });
      expect(within(row).getByText("ACTION-LABEL-MARKER")).toBeInTheDocument();
      expect(within(row).queryByText("Flock depleted")).not.toBeInTheDocument();
      expect(within(row).queryByText("Flock.Deplete")).not.toBeInTheDocument();
    });
  });

  // Proves the filter option TEXT reads enums:auditAction.* through
  // auditActionLabel(a), while the option's VALUE (the server filter param)
  // stays the untouched raw code — the two must read from different places
  // (label vs. raw value) and this is the site that would break if someone
  // "simplified" the option to `{a}` for both.
  it("reads the filter option text from the enums catalog while its value stays the raw code", async () => {
    await withOverride("enums", "auditAction.User.Update", "OPTION-LABEL-MARKER", async () => {
      render(<AuditPage />);
      await screen.findByText("No audit events yet.");
      const option = screen.getByRole("option", { name: "OPTION-LABEL-MARKER" }) as HTMLOptionElement;
      expect(option.value).toBe("User.Update");
      expect(screen.queryByRole("option", { name: "User updated" })).not.toBeInTheDocument();
    });
  });

  // Proves the entity table cell reads enums:entityType.* through
  // entityTypeLabel(e.entityType) rather than rendering e.entityType raw.
  // EVENT_B's entityType is "User" (also an identity label in en, so this
  // override is the only way to distinguish "reads the catalog" from
  // "renders the raw value that happens to equal the label").
  it("reads the entity cell from the enums catalog via entityTypeLabel, not the raw wire value", async () => {
    mockListAuditEvents.mockResolvedValue([EVENT_B]); // entityType: "User", entityId: "u9abcdef-0000"
    await withOverride("enums", "entityType.User", "ENTITY-LABEL-MARKER", async () => {
      render(<AuditPage />);
      const row = await screen.findByRole("row", { name: /manager@farm\.test/ });
      expect(within(row).getByText("ENTITY-LABEL-MARKER u9abcdef")).toBeInTheDocument();
      expect(within(row).queryByText("User u9abcdef")).not.toBeInTheDocument();
    });
  });
});
