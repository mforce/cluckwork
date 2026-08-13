import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, within, act, fireEvent } from "@testing-library/react";
import { Link, MemoryRouter, Route, Routes } from "react-router";
import { AuditPage, isFetchStale } from "./AuditPage";
import { listAuditEvents } from "../api/cluckwork";
import type { AuditEvent } from "../api/cluckwork";
import i18n from "../i18n";

// AuditPage's only network dep is listAuditEvents; mock that seam so the screen
// renders against controlled data — no network, no backend. ApiError stays real
// (from ../api/client, unmocked) so errText's instanceof checks still hold.
//
// Role gating is intentionally NOT tested: AuditPage reads useAuth from
// neither — it renders identically for any session. The #93 comment is
// explicit that the admin-only guarantee is enforced by the route/API, not the
// component, so there is no in-component gating behavior to assert. A plain
// AuthProvider-less render is still sufficient for that reason — but #493
// added useSearchParams, so every render now needs a Router ancestor
// (MemoryRouter, not the full renderWithProviders — no Auth/Farm dependency
// here, only routing).
vi.mock("../api/cluckwork", () => ({
  listAuditEvents: vi.fn(),
}));

const mockListAuditEvents = vi.mocked(listAuditEvents);

function renderAudit(route = "/audit") {
  return render(
    <MemoryRouter initialEntries={[route]}>
      <AuditPage />
    </MemoryRouter>,
  );
}

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
    renderAudit();

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
    renderAudit();
    expect(await screen.findByText("No audit events yet.")).toBeInTheDocument();
  });

  it("loads the first page on mount with no action filter, limit 100, offset 0", async () => {
    mockListAuditEvents.mockResolvedValue([]);
    renderAudit();
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
    renderAudit();
    await screen.findByText("No audit events yet.");
    const option = screen.getByRole("option", { name: "User updated" }) as HTMLOptionElement;
    expect(option.value).toBe("User.Update");
    expect(screen.queryByRole("option", { name: "User.Update" })).not.toBeInTheDocument();
  });

  it("offers both password actions as filterable, labelled and value-preserved (#165)", async () => {
    renderAudit();
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
    renderAudit();
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
    renderAudit();

    const row = await screen.findByRole("row", { name: /admin@farm\.test/ });
    // Action cell: friendly label, not the raw "Account.SetLogo".
    expect(within(row).getByText("Farm logo set")).toBeInTheDocument();
    // Entity cell: entityTypeLabel("FarmLogo") + first 8 chars of the id.
    expect(within(row).getByText("Farm logo fl123456")).toBeInTheDocument();
    expect(within(row).queryByText("FarmLogo fl123456")).not.toBeInTheDocument();
  });

  it("maps each audit event's actor, action, entity and reason into its row", async () => {
    mockListAuditEvents.mockResolvedValue([EVENT_A, EVENT_B]);
    renderAudit();

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
    renderAudit();
    await screen.findByRole("row", { name: /admin@farm\.test/ });
    // #93: the audit trail is deliberately read-only — no adjust/void/delete
    // controls — and 'load more' only appears when a full page came back.
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });
});

describe("AuditPage filter", () => {
  it("re-queries listAuditEvents with the chosen action when the filter changes", async () => {
    mockListAuditEvents.mockResolvedValue([]);
    renderAudit();
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
    renderAudit();

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
      renderAudit();
      await screen.findByText("No audit events yet."); // let the mount load settle
      expect(screen.getByRole("heading", { name: "HEADING-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Audit log" })).not.toBeInTheDocument();
    });
  });

  // #493 — entity-scoped heading. entityType is interpolated via
  // entityTypeLabel(), not literal English, so the marker must carry the
  // {{entityType}} placeholder through to prove interpolation still works
  // under the overridden template, not just that SOME text renders.
  it("reads the scoped heading from the audit catalog, not a hardcoded literal", async () => {
    await withOverride("audit", "scopedHeading", "SCOPED-MARKER {{entityType}}", async () => {
      mockListAuditEvents.mockResolvedValue([
        { ...EVENT_A, entityType: "Flock", entityId: SCOPED_ENTITY_ID },
      ]);
      renderAudit(`/audit?entityId=${SCOPED_ENTITY_ID}`);
      expect(await screen.findByRole("heading", { name: "SCOPED-MARKER Flock" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Flock history" })).not.toBeInTheDocument();
    });
  });

  it("reads the scoped-heading fallback from the audit catalog, not a hardcoded literal", async () => {
    await withOverride("audit", "scopedHeadingFallback", "FALLBACK-MARKER", async () => {
      mockListAuditEvents.mockResolvedValue([]);
      renderAudit(`/audit?entityId=${SCOPED_ENTITY_ID}`);
      expect(await screen.findByRole("heading", { name: "FALLBACK-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Record history" })).not.toBeInTheDocument();
    });
  });

  it("reads the scoped empty message from the audit catalog, not a hardcoded literal", async () => {
    await withOverride("audit", "scopedEmptyMessage", "SCOPED-EMPTY-MARKER", async () => {
      mockListAuditEvents.mockResolvedValue([]);
      renderAudit(`/audit?entityId=${SCOPED_ENTITY_ID}`);
      expect(await screen.findByText("SCOPED-EMPTY-MARKER")).toBeInTheDocument();
      expect(screen.queryByText("No audit events for this record yet.")).not.toBeInTheDocument();
    });
  });

  it("reads the intro prose from the audit catalog, not a hardcoded literal", async () => {
    await withOverride("audit", "intro", "INTRO-MARKER", async () => {
      renderAudit();
      await screen.findByText("No audit events yet.");
      expect(screen.getByText("INTRO-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/Every corrective, destructive/)).not.toBeInTheDocument();
    });
  });

  it("reads the action filter label from the audit catalog, not a hardcoded literal", async () => {
    await withOverride("audit", "actionFilterLabel", "FILTER-LABEL-MARKER", async () => {
      renderAudit();
      await screen.findByText("No audit events yet.");
      // `<label>{t(...)}<select>…</select></label>` — getByLabelText matches
      // the label's own text only (excluding the wrapped select's rendered
      // option text), same pattern as UsersPage's role picker.
      expect(screen.getByLabelText("FILTER-LABEL-MARKER")).toBeInTheDocument();
    });
  });

  it("reads the 'All actions' option from the audit catalog, not a hardcoded literal", async () => {
    await withOverride("audit", "allActionsOption", "ALL-ACTIONS-MARKER", async () => {
      renderAudit();
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
        const { unmount } = renderAudit();
        await screen.findByRole("row", { name: /admin@farm\.test/ });
        expect(screen.getByRole("columnheader", { name: marker })).toBeInTheDocument();
        expect(screen.queryByRole("columnheader", { name: original })).not.toBeInTheDocument();
        unmount();
      });
    }
  });

  it("reads the empty-state message from the audit catalog, not a hardcoded literal", async () => {
    await withOverride("audit", "emptyMessage", "EMPTY-MARKER", async () => {
      renderAudit();
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
      renderAudit();
      expect(await screen.findByRole("button", { name: "LOAD-MORE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "load more" })).not.toBeInTheDocument();
    });
  });

  // Proves the action table cell reads enums:auditAction.* through
  // auditActionLabel(e.action) rather than rendering e.action raw.
  it("reads the action cell from the enums catalog via auditActionLabel, not the raw wire code", async () => {
    mockListAuditEvents.mockResolvedValue([EVENT_A]); // action: "Flock.Deplete"
    await withOverride("enums", "auditAction.Flock.Deplete", "ACTION-LABEL-MARKER", async () => {
      renderAudit();
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
      renderAudit();
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
      renderAudit();
      const row = await screen.findByRole("row", { name: /manager@farm\.test/ });
      expect(within(row).getByText("ENTITY-LABEL-MARKER u9abcdef")).toBeInTheDocument();
      expect(within(row).queryByText("User u9abcdef")).not.toBeInTheDocument();
    });
  });
});

// ---------------------------------------------------------------------------
// Entity-scoped mode (#493, Slice 1)
// ---------------------------------------------------------------------------

// A real canonical GUID, distinct from the fixture events' short ids above
// (which are fine for display but would fail isLikelyGuid as a route param).
const SCOPED_ENTITY_ID = "f1234567-89ab-4cde-8f01-234567890abc";

describe("AuditPage entity-scoped mode (#493)", () => {
  it("passes entityId to listAuditEvents when present in the URL", async () => {
    mockListAuditEvents.mockResolvedValue([]);
    renderAudit(`/audit?entityId=${SCOPED_ENTITY_ID}`);
    await screen.findByText("No audit events for this record yet.");
    expect(mockListAuditEvents).toHaveBeenCalledWith(
      expect.objectContaining({ entityId: SCOPED_ENTITY_ID }),
    );
  });

  it("renders a scoped heading naming the entity type once the first page has loaded", async () => {
    mockListAuditEvents.mockResolvedValue([
      { ...EVENT_A, entityType: "Flock", entityId: SCOPED_ENTITY_ID },
    ]);
    renderAudit(`/audit?entityId=${SCOPED_ENTITY_ID}`);
    expect(await screen.findByRole("heading", { name: "Flock history" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Audit log" })).not.toBeInTheDocument();
  });

  it("falls back to the generic 'Record history' heading when entityId is present but zero rows return", async () => {
    mockListAuditEvents.mockResolvedValue([]);
    renderAudit(`/audit?entityId=${SCOPED_ENTITY_ID}`);
    expect(await screen.findByRole("heading", { name: "Record history" })).toBeInTheDocument();
  });

  it("treats a malformed entityId as absent: does not call listAuditEvents with it, renders the unscoped view", async () => {
    mockListAuditEvents.mockResolvedValue([]);
    renderAudit("/audit?entityId=not-a-guid");
    await screen.findByText("No audit events yet.");
    // Not objectContaining({ entityId: undefined }) — that matches whether the
    // key is explicitly undefined OR simply absent, so it can't actually prove
    // the malformed value was dropped rather than just never checked. Read the
    // real call args directly instead (review round 1 finding).
    const lastCall = mockListAuditEvents.mock.calls.at(-1)?.[0];
    expect(lastCall?.entityId).toBeUndefined();
    expect(screen.getByRole("heading", { name: "Audit log" })).toBeInTheDocument();
  });

  // codex review of #516 — a pasted/hand-typed uppercase GUID is
  // syntactically valid (the guard regex is case-insensitive) and the API
  // accepts it, but its response's entityId is always lowercase (a .NET
  // Guid serialized by System.Text.Json). Without normalizing the URL
  // value, it would build a different fetchPage identity than the
  // lowercase form does, which isFetchStale treats as a genuinely
  // different query — this must NOT get stuck on "loading" forever.
  it("normalizes an uppercase entityId so it matches the lowercase entityId the API returns, and doesn't stick on loading", async () => {
    const upper = SCOPED_ENTITY_ID.toUpperCase();
    mockListAuditEvents.mockResolvedValue([
      { ...EVENT_A, entityType: "Flock", entityId: SCOPED_ENTITY_ID }, // lowercase, as the API returns it
    ]);
    renderAudit(`/audit?entityId=${upper}`);

    expect(await screen.findByRole("heading", { name: "Flock history" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Record history" })).not.toBeInTheDocument();
    expect(await screen.findByRole("row", { name: /admin@farm\.test/ })).toBeInTheDocument();
    expect(mockListAuditEvents).toHaveBeenCalledWith(
      expect.objectContaining({ entityId: SCOPED_ENTITY_ID }), // normalized, not the raw uppercase URL value
    );
  });

  it("preserves entityId in the URL when the action filter changes while scoped (updateActionFilter merge)", async () => {
    mockListAuditEvents.mockResolvedValue([]);
    renderAudit(`/audit?entityId=${SCOPED_ENTITY_ID}`);
    await screen.findByText("No audit events for this record yet.");

    await act(async () => {
      fireEvent.change(screen.getByRole("combobox"), { target: { value: "Flock.Deplete" } });
    });

    expect(mockListAuditEvents).toHaveBeenLastCalledWith(
      expect.objectContaining({ action: "Flock.Deplete", entityId: SCOPED_ENTITY_ID }),
    );
  });

  it("carries only the action filter (no stray entityId) when changed from the unscoped view", async () => {
    mockListAuditEvents.mockResolvedValue([]);
    renderAudit("/audit");
    await screen.findByText("No audit events yet.");

    await act(async () => {
      fireEvent.change(screen.getByRole("combobox"), { target: { value: "Flock.Deplete" } });
    });

    expect(mockListAuditEvents).toHaveBeenLastCalledWith(
      expect.objectContaining({ action: "Flock.Deplete", entityId: undefined }),
    );
  });

  // #493, Slice 2 — the bug this test exists to catch: usePagedList leaves
  // the previous page's rows in place until the new one lands, so reading
  // rows[0] mid-reload would show the PREVIOUS entity's type. Manually
  // controlled (deferred) promise, not one that resolves same-tick, so the
  // reloading=true window is actually observable to the test.
  it("falls back to the generic heading while a reload is in flight, not the previous entity's type", async () => {
    mockListAuditEvents.mockResolvedValueOnce([
      { ...EVENT_A, entityType: "Flock", entityId: SCOPED_ENTITY_ID },
    ]);
    renderAudit(`/audit?entityId=${SCOPED_ENTITY_ID}`);
    expect(await screen.findByRole("heading", { name: "Flock history" })).toBeInTheDocument();

    let resolveReload!: (events: AuditEvent[]) => void;
    mockListAuditEvents.mockReturnValueOnce(new Promise<AuditEvent[]>((r) => (resolveReload = r)));

    await act(async () => {
      fireEvent.change(screen.getByRole("combobox"), { target: { value: "Flock.Deplete" } });
    });

    // The reload is in flight: the heading must NOT still say "Flock
    // history" — that would be the previous page's stale entityType.
    expect(screen.getByRole("heading", { name: "Record history" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Flock history" })).not.toBeInTheDocument();

    await act(async () => {
      resolveReload([{ ...EVENT_A, entityType: "Flock", entityId: SCOPED_ENTITY_ID }]);
    });
    expect(await screen.findByRole("heading", { name: "Flock history" })).toBeInTheDocument();
  });

  it("hides the entity column when scoped; shows it when unscoped", async () => {
    mockListAuditEvents.mockResolvedValue([
      { ...EVENT_A, entityType: "Flock", entityId: SCOPED_ENTITY_ID },
    ]);
    const { unmount } = renderAudit(`/audit?entityId=${SCOPED_ENTITY_ID}`);
    const scopedRow = await screen.findByRole("row", { name: /admin@farm\.test/ });
    expect(screen.queryByRole("columnheader", { name: "Entity" })).not.toBeInTheDocument();
    // The header and the row's own <td> are two separate gates in the JSX
    // (review round 1 finding) — asserting only the header would miss a
    // regression that drops the header but leaves the cell rendered.
    expect(within(scopedRow).queryByText(/Flock f1234567/)).not.toBeInTheDocument();
    unmount();

    renderAudit("/audit");
    const unscopedRow = await screen.findByRole("row", { name: /admin@farm\.test/ });
    expect(screen.getByRole("columnheader", { name: "Entity" })).toBeInTheDocument();
    expect(within(unscopedRow).getByText(/Flock f1234567/)).toBeInTheDocument();
  });

  it("shows a scoped empty message, distinct from the global one, when the record has no events", async () => {
    mockListAuditEvents.mockResolvedValue([]);
    renderAudit(`/audit?entityId=${SCOPED_ENTITY_ID}`);
    expect(await screen.findByText("No audit events for this record yet.")).toBeInTheDocument();
    expect(screen.queryByText("No audit events yet.")).not.toBeInTheDocument();
  });
});

// #493, Slice 4 — Flow A': clicking a DIFFERENT record's link while already
// on /audit, with no remount. Gate 3's original test plan covered a fresh
// navigation and an action-filter change while scoped, but never this one —
// the primary record-to-record browsing flow — found missing during Slice 4
// review of the design docs, not by a code review of shipped code.
const OTHER_ENTITY_ID = "b9876543-21fe-4dc0-8b12-fedcba098765";
const THIRD_ENTITY_ID = "c1112223-3344-4556-8778-899900112233";
const FOURTH_ENTITY_ID = "d2223334-4455-4667-8889-900011223344";

// A real <Routes> tree, matching App.tsx's own single-route mapping to
// AuditPage (review round 1: a sibling Link with no Routes couldn't prove
// AuditPage stays mounted across the navigation, only that it happens to).
// Navigating /audit?entityId=A -> /audit?entityId=B never re-matches the
// route (the pathname doesn't change), so AuditPage genuinely never
// remounts here, same as production.
function renderAuditWithSwitchLink(route: string) {
  return render(
    <MemoryRouter initialEntries={[route]}>
      <Link to={`/audit?entityId=${OTHER_ENTITY_ID}`}>switch record</Link>
      <Routes>
        <Route path="/audit" element={<AuditPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

// codex review of #516 — a THIRD link, for the delayed double-switch
// scenario that broke the ref-based version of this fix: switching to B,
// then to C before B's fetch resolves.
function renderAuditWithTwoSwitchLinks(route: string) {
  return render(
    <MemoryRouter initialEntries={[route]}>
      <Link to={`/audit?entityId=${OTHER_ENTITY_ID}`}>switch to B</Link>
      <Link to={`/audit?entityId=${THIRD_ENTITY_ID}`}>switch to C</Link>
      <Routes>
        <Route path="/audit" element={<AuditPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

// Self-review addition (not a codex finding) — a third switch, belt-and-
// suspenders on top of the B/C test above. Not closing a gap the ticket-system reasoning
// doesn't already cover structurally (any switch's fetch, once superseded,
// is a no-op regardless of resolution order — verified against
// usePagedList.ts's seq guard), but cheap to pin explicitly rather than
// leave to inference given how many rounds this exact mechanism has been
// through.
function renderAuditWithThreeSwitchLinks(route: string) {
  return render(
    <MemoryRouter initialEntries={[route]}>
      <Link to={`/audit?entityId=${OTHER_ENTITY_ID}`}>switch to B</Link>
      <Link to={`/audit?entityId=${THIRD_ENTITY_ID}`}>switch to C</Link>
      <Link to={`/audit?entityId=${FOURTH_ENTITY_ID}`}>switch to D</Link>
      <Routes>
        <Route path="/audit" element={<AuditPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe("AuditPage Flow A' — switching records without leaving /audit (#493)", () => {
  it("re-scopes to the new record: re-fires the fetch and updates the heading, not stale on the old one", async () => {
    mockListAuditEvents.mockResolvedValueOnce([
      { ...EVENT_A, entityType: "Flock", entityId: SCOPED_ENTITY_ID },
    ]);
    renderAuditWithSwitchLink(`/audit?entityId=${SCOPED_ENTITY_ID}`);
    expect(await screen.findByRole("heading", { name: "Flock history" })).toBeInTheDocument();

    mockListAuditEvents.mockResolvedValueOnce([
      { ...EVENT_B, entityType: "SalesOrder", entityId: OTHER_ENTITY_ID },
    ]);
    await act(async () => {
      fireEvent.click(screen.getByRole("link", { name: "switch record" }));
    });

    expect(await screen.findByRole("heading", { name: "Sales order history" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Flock history" })).not.toBeInTheDocument();
    expect(mockListAuditEvents).toHaveBeenLastCalledWith(
      expect.objectContaining({ entityId: OTHER_ENTITY_ID }),
    );
    // Not stale-blended: the row shown belongs to the NEW entity only.
    expect(await screen.findByRole("row", { name: /manager@farm\.test/ })).toBeInTheDocument();
  });

  it("shows the generic fallback while the switch is in flight, not the previous record's type", async () => {
    mockListAuditEvents.mockResolvedValueOnce([
      { ...EVENT_A, entityType: "Flock", entityId: SCOPED_ENTITY_ID },
    ]);
    renderAuditWithSwitchLink(`/audit?entityId=${SCOPED_ENTITY_ID}`);
    expect(await screen.findByRole("heading", { name: "Flock history" })).toBeInTheDocument();

    let resolveSwitch!: (events: AuditEvent[]) => void;
    mockListAuditEvents.mockReturnValueOnce(new Promise<AuditEvent[]>((r) => (resolveSwitch = r)));
    await act(async () => {
      fireEvent.click(screen.getByRole("link", { name: "switch record" }));
    });

    expect(screen.getByRole("heading", { name: "Record history" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Flock history" })).not.toBeInTheDocument();

    await act(async () => {
      resolveSwitch([{ ...EVENT_B, entityType: "SalesOrder", entityId: OTHER_ENTITY_ID }]);
    });
    expect(await screen.findByRole("heading", { name: "Sales order history" })).toBeInTheDocument();
    // Not vacuous (review round 1): a component that flipped to the generic
    // fallback WITHOUT actually re-fetching would also pass the assertions
    // above. Pin that the switch really did fire a new request for the new
    // entity, not just an incidental heading change.
    expect(mockListAuditEvents).toHaveBeenLastCalledWith(
      expect.objectContaining({ entityId: OTHER_ENTITY_ID }),
    );
  });

});

// codex review of #516 — the bug the Flow A' tests above can't see, and the
// reason this file went through three shapes of the same mechanism. A
// content-based check (comparing rows' own entityId against the current
// scope) caught the common case but review kept finding variants it
// missed: an empty stale page (no row to compare), and leaving a scope
// entirely (entityId -> undefined, which a content check exits early on by
// design). Two misses of the same shape means the method was wrong —
// isFetchStale compares fetchPage's own reference instead, which closes
// every variant uniformly. A component test can't reliably observe the
// one-render window this guards: RTL's fireEvent wraps the click in a
// synchronous act() that flushes passive effects before any assertion runs
// (confirmed by mutation on an earlier version of this fix: a test that
// awaited the click and checked the DOM afterward passed even with the fix
// reverted). Testing the extracted pure function directly sidesteps the
// timing question entirely.
describe("isFetchStale (#493)", () => {
  it("is not stale when comparing a fetchPage reference to itself", () => {
    const fn = () => Promise.resolve([]);
    expect(isFetchStale(fn, fn)).toBe(false);
  });

  it("is stale when the references differ, regardless of what they'd return", () => {
    const a = () => Promise.resolve([]);
    const b = () => Promise.resolve([]);
    expect(isFetchStale(a, b)).toBe(true);
  });
});

// Integration coverage for the specific scenarios the OLD content-based
// checks missed — proven via settled state (not a single-render freeze,
// which isFetchStale's direct unit tests above already cover): the
// mechanism must never leave the page stuck, and must never show one
// scope's data under another scope's heading, across every transition.
describe("AuditPage stale-scope coverage beyond Flow A' (#493)", () => {
  it("leaving a scope (an explicit unscoped Link, not just typing the URL) settles on the global heading and rows, not stuck on the old scope's data", async () => {
    mockListAuditEvents.mockResolvedValueOnce([
      { ...EVENT_A, entityType: "Flock", entityId: SCOPED_ENTITY_ID },
    ]);
    render(
      <MemoryRouter initialEntries={[`/audit?entityId=${SCOPED_ENTITY_ID}`]}>
        <Link to="/audit">leave scope</Link>
        <Routes>
          <Route path="/audit" element={<AuditPage />} />
        </Routes>
      </MemoryRouter>,
    );
    expect(await screen.findByRole("heading", { name: "Flock history" })).toBeInTheDocument();

    mockListAuditEvents.mockResolvedValueOnce([EVENT_A, EVENT_B]); // the global, multi-entity log
    await act(async () => {
      fireEvent.click(screen.getByRole("link", { name: "leave scope" }));
    });

    expect(await screen.findByRole("heading", { name: "Audit log" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Flock history" })).not.toBeInTheDocument();
    // The entity column is back (unscoped) and BOTH rows show — not left
    // showing only the previous single-entity scope's row under the global
    // heading, which is what the old content-based check could do (it never
    // treated entityId -> undefined as stale).
    expect(await screen.findByRole("row", { name: /admin@farm\.test/ })).toBeInTheDocument();
    expect(screen.getByRole("row", { name: /manager@farm\.test/ })).toBeInTheDocument();
    expect(mockListAuditEvents).toHaveBeenLastCalledWith(
      expect.objectContaining({ entityId: undefined }),
    );
  });

  // Honest framing (review round): this is a settled-state regression
  // guard, not a test that discriminates the fixed mechanism from the OLD
  // content-based one — with a synchronously-resolving mock inside
  // act(async () => {...}), everything flushes before any assertion runs
  // either way, so the old mechanism would pass this exact test too. The
  // empty-page window it used to miss is closed by isFetchStale's own unit
  // tests plus the reasoning documented on the commit-state site in
  // AuditPage.tsx (isFetchStale doesn't depend on row content at all, so
  // there's no timing-sensitive window left specific to an empty page to
  // observe here). This test's job is narrower and still real: prove the
  // empty-page → switch sequence never wedges the page.
  it("a scoped view that resolves to a genuinely empty page still settles correctly on a later switch", async () => {
    mockListAuditEvents.mockResolvedValueOnce([]); // record A: genuinely no events
    renderAuditWithSwitchLink(`/audit?entityId=${SCOPED_ENTITY_ID}`);
    expect(await screen.findByText("No audit events for this record yet.")).toBeInTheDocument();

    mockListAuditEvents.mockResolvedValueOnce([
      { ...EVENT_B, entityType: "SalesOrder", entityId: OTHER_ENTITY_ID },
    ]);
    await act(async () => {
      fireEvent.click(screen.getByRole("link", { name: "switch record" }));
    });

    expect(await screen.findByRole("heading", { name: "Sales order history" })).toBeInTheDocument();
    expect(await screen.findByRole("row", { name: /manager@farm\.test/ })).toBeInTheDocument();
  });

  // codex review of #516, round 5 — the exact bug a ref-based version of
  // this fix shipped and broke on: switch to B, then switch to C before B's
  // fetch resolves. B is deliberately never resolved here — it doesn't need
  // to be; a superseded fetch's completion is inert regardless (see the
  // ticket-system reasoning documented at the commit-state site in
  // AuditPage.tsx). The ref-based version got stuck on "Record history"
  // (the generic fallback) forever once C's OWN fetch resolved, because
  // mutating a ref doesn't schedule the extra render the correction needed.
  it("recovers after switching to a second record before the first one's fetch resolves, not stuck on the generic fallback forever", async () => {
    mockListAuditEvents.mockResolvedValueOnce([
      { ...EVENT_A, entityType: "Flock", entityId: SCOPED_ENTITY_ID },
    ]);
    renderAuditWithTwoSwitchLinks(`/audit?entityId=${SCOPED_ENTITY_ID}`);
    expect(await screen.findByRole("heading", { name: "Flock history" })).toBeInTheDocument();

    mockListAuditEvents.mockReturnValueOnce(new Promise<AuditEvent[]>(() => {})); // B: never settles
    await act(async () => {
      fireEvent.click(screen.getByRole("link", { name: "switch to B" }));
    });
    expect(screen.getByRole("heading", { name: "Record history" })).toBeInTheDocument();

    let resolveC!: (events: AuditEvent[]) => void;
    mockListAuditEvents.mockReturnValueOnce(new Promise<AuditEvent[]>((r) => (resolveC = r)));
    await act(async () => {
      fireEvent.click(screen.getByRole("link", { name: "switch to C" }));
    });
    expect(screen.getByRole("heading", { name: "Record history" })).toBeInTheDocument();

    await act(async () => {
      resolveC([{ ...EVENT_B, entityType: "SalesOrder", entityId: THIRD_ENTITY_ID }]);
    });

    expect(await screen.findByRole("heading", { name: "Sales order history" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Record history" })).not.toBeInTheDocument();
    expect(await screen.findByRole("row", { name: /manager@farm\.test/ })).toBeInTheDocument();
  });

  it("recovers correctly through a THREE-way switch (B, then C, then D — B and C both superseded before either settles)", async () => {
    mockListAuditEvents.mockResolvedValueOnce([
      { ...EVENT_A, entityType: "Flock", entityId: SCOPED_ENTITY_ID },
    ]);
    renderAuditWithThreeSwitchLinks(`/audit?entityId=${SCOPED_ENTITY_ID}`);
    expect(await screen.findByRole("heading", { name: "Flock history" })).toBeInTheDocument();

    mockListAuditEvents.mockReturnValueOnce(new Promise<AuditEvent[]>(() => {})); // B: never settles
    await act(async () => {
      fireEvent.click(screen.getByRole("link", { name: "switch to B" }));
    });

    mockListAuditEvents.mockReturnValueOnce(new Promise<AuditEvent[]>(() => {})); // C: never settles either
    await act(async () => {
      fireEvent.click(screen.getByRole("link", { name: "switch to C" }));
    });
    expect(screen.getByRole("heading", { name: "Record history" })).toBeInTheDocument();

    let resolveD!: (events: AuditEvent[]) => void;
    mockListAuditEvents.mockReturnValueOnce(new Promise<AuditEvent[]>((r) => (resolveD = r)));
    await act(async () => {
      fireEvent.click(screen.getByRole("link", { name: "switch to D" }));
    });
    expect(screen.getByRole("heading", { name: "Record history" })).toBeInTheDocument();

    await act(async () => {
      resolveD([{ ...EVENT_A, entityType: "Expense", entityId: FOURTH_ENTITY_ID, actorEmail: "d-actor@farm.test" }]);
    });

    expect(await screen.findByRole("heading", { name: "Expense history" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Record history" })).not.toBeInTheDocument();
    expect(await screen.findByRole("row", { name: /d-actor@farm\.test/ })).toBeInTheDocument();
  });
});
