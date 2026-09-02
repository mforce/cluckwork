import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, within, act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { GlossaryLink } from "../components/GlossaryLink";
import { renderWithProviders } from "../test/renderWithProviders";
import { GLOSSARY } from "./helpGlossary";
import { HelpPage } from "./HelpPage";
import i18n from "../i18n";
import { en } from "../i18n/en";
import { es } from "../i18n/es";
import { tl } from "../i18n/tl";

// Minimal IntersectionObserver stub (jsdom has none): capture the callback so a
// test can simulate a section scrolling into view.
type IOEntry = { isIntersecting: boolean; target: { id: string }; boundingClientRect: { top: number } };
let ioCallback: ((entries: IOEntry[]) => void) | null = null;

beforeEach(() => {
  ioCallback = null;
  class MockIO {
    constructor(cb: (entries: IOEntry[]) => void) { ioCallback = cb; }
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  vi.stubGlobal("IntersectionObserver", MockIO);
});
afterEach(() => vi.unstubAllGlobals());

describe("HelpPage", () => {
  it("renders the guide with a contents rail linking to its sections", () => {
    render(<HelpPage />);
    expect(screen.getByRole("heading", { name: "Help", level: 2 })).toBeInTheDocument();

    const toc = screen.getByRole("navigation", { name: "Help contents" });
    expect(within(toc).getByRole("link", { name: "The daily loop" })).toHaveAttribute("href", "#daily-loop");

    expect(screen.getByRole("heading", { name: "The daily loop", level: 3 })).toBeInTheDocument();
    expect(screen.getByText("FIFO", { selector: "dt a" })).toBeInTheDocument();
  });

  it("keeps the contents rail and the sections in step, in document order", () => {
    // The rail is hand-maintained beside the sections it points at ("must
    // mirror the <h3 id=...> sections below, in document order"). A section
    // added without its entry is invisible to anyone navigating by contents,
    // and an entry without its section is a dead link — neither shows up in a
    // test that only asserts the sections it happens to name.
    const { container } = render(<HelpPage />);
    const toc = screen.getByRole("navigation", { name: "Help contents" });

    const linked = within(toc).getAllByRole("link")
      .map((a) => a.getAttribute("href")?.slice(1));
    const sections = Array.from(container.querySelectorAll("h3[id]")).map((h) => h.id);

    expect(sections.length).toBeGreaterThan(0);
    expect(linked).toEqual(sections);
  });

  it("documents farm settings, the currency lock and the logo (#123)", () => {
    render(<HelpPage />);
    expect(screen.getByRole("heading", { name: "Farm settings (admin)", level: 3 })).toBeInTheDocument();
    expect(screen.getByText("Farm settings", { selector: "dt a" })).toBeInTheDocument();
    expect(screen.getByText("Currency lock", { selector: "dt a" })).toBeInTheDocument();
    expect(screen.getByText("Farm logo", { selector: "dt a" })).toBeInTheDocument();
  });

  it("documents page loading in Getting around and the in-app glossary (#595)", () => {
    render(<HelpPage />);
    expect(screen.getByText(/first time you open a screen after starting or updating Cluckwork/i))
      .toBeInTheDocument();
    expect(screen.getByText("Page loading", { selector: "dt a" })).toBeInTheDocument();

    for (const catalog of [es, tl]) {
      expect(catalog.help.gettingAroundPageLoading).toBeTruthy();
      expect(catalog.help.gettingAroundPageLoading).not.toBe(en.help.gettingAroundPageLoading);
      expect(catalog.help.glossaryPageLoadingTerm).not.toBe(en.help.glossaryPageLoadingTerm);
      expect(catalog.help.glossaryPageLoadingDef).not.toBe(en.help.glossaryPageLoadingDef);
    }
  });

  // #612 review fix — the guidance must name only the roles that actually
  // confirm farm-wide regardless of this setting (Owner, Manager, Sales), not
  // list ReadOnly among them as if it too confirmed farm-wide — ReadOnly
  // cannot confirm a sale at all.
  it("documents worker sale allocation under Farm settings, naming only Owner/Manager/Sales as farm-wide confirmers (#612)", () => {
    render(<HelpPage />);
    // "Worker sale allocation" also names the glossary row further down the
    // page — the Farm settings guidance item is the FIRST match.
    const item = screen.getAllByText(/Worker sale allocation/)[0]!.closest("li")!;
    expect(item).toHaveTextContent("Owner");
    expect(item).toHaveTextContent("Manager");
    expect(item).toHaveTextContent("Sales");
    // The old, wrong copy listed Read-only as a fourth farm-wide confirmer.
    expect(item).not.toHaveTextContent(/Sales,? and Read-?only/i);
    expect(item).not.toHaveTextContent(/Sales,? Read-?only/i);

    // Never claims ReadOnly confirms, in any catalog.
    for (const catalog of [en, es, tl]) {
      const text = catalog.help.farmSettingsWorkerSaleAllocation;
      expect(text).not.toMatch(/Sales,? and Read-?only/i);
      expect(text).not.toMatch(/Sales,? Read-?only/i);
      expect(text).not.toMatch(/Ventas y Solo lectura/i);
      expect(text).not.toMatch(/Sales, at Read-only/i);
    }
  });

  it("documents the busy-save indicator (#236) via the catalog key, not a drifting literal", () => {
    // The line reads common:workingHint so it can never drift from the
    // BusyButton announcement it explains — swap the catalog value and the
    // page must follow (a hardcoded copy of the sentence would not).
    const original = i18n.getResource("en", "common", "workingHint") as string;
    i18n.addResource("en", "common", "workingHint", "WORKING-HINT-MARKER");
    try {
      render(<HelpPage />);
      expect(screen.getByText("WORKING-HINT-MARKER")).toBeInTheDocument();
    } finally {
      i18n.addResource("en", "common", "workingHint", original);
    }
    // And the real copy renders by default.
    render(<HelpPage />);
    expect(screen.getByText(/A spinning button means the save is still working/)).toBeInTheDocument();
  });

  it("says the page behind a popup is out of reach, in every catalog (#482)", () => {
    // The behaviour: everything except the topmost dialog is inert, so the
    // background is unreachable by pointer AND by a screen reader — and Escape
    // closes the dialog the user is in, not every open one.
    render(<HelpPage />);
    expect(screen.getByText(/the page behind it/i)).toBeInTheDocument();
    expect(screen.getByText(/Escape closes the popup you are working in/i)).toBeInTheDocument();

    for (const lng of ["es", "tl"] as const) {
      const value = i18n.getResource(lng, "help", "dialogsModal") as string;
      expect(value).toBeTruthy();
      expect(value).not.toBe(i18n.getResource("en", "help", "dialogsModal"));
    }
  });

  // codex on #483: the string-concatenated en catalog entry lost the space
  // between two adjacent <Trans> segments, rendering "Daily entry,Water"
  // with nothing between the names. Nothing asserted the joined text before,
  // so it shipped silently — this is that assertion.
  it("keeps a space between adjacent inline-form names", () => {
    // The two names sit in separate <strong> elements either side of the
    // regression's missing space, so the joined text is not any single
    // node's own — read the item's full textContent instead of getByText,
    // which only matches within one node.
    render(<HelpPage />);
    // "Daily entry" and "Water" both appear elsewhere on the page (nav, other
    // sections); "recording an expense" is unique to this list item.
    const item = screen.getByText(/recording an expense/).closest("li");
    expect(item).not.toBeNull();
    expect(item!.textContent).toMatch(/Daily entry,\s+Water/);
  });

  it("says where a failure message appears, in every catalog (#479)", () => {
    // #478 narrowed this to "a failed save explains itself inside the form"
    // because only Sales had the two-slot split then — CustomersPage's
    // background balance-load failure landed inside its own New customer
    // dialog, the exact opposite of the wider claim. #479 gave every dialog
    // screen its own page/dialog split (#489/#491), so the full behaviour —
    // a form's failure rendered inside that form, the screen's on the
    // screen, and closing the form dropping only its own — now holds
    // app-wide. Asserted per catalog because the i18n policy ships es/tl
    // with the English, and a missing key would render the key.
    render(<HelpPage />);
    expect(screen.getByText(/if you were filling in a pop-up form, it appears inside that form/i))
      .toBeInTheDocument();
    // …and closing it drops that message rather than moving it to the screen.
    expect(screen.getByText(/Closing the form drops its message/i)).toBeInTheDocument();

    for (const lng of ["es", "tl"] as const) {
      const value = i18n.getResource(lng, "help", "gettingAroundWhereMessagesAppear") as string;
      expect(value).toBeTruthy();
      expect(value).not.toBe(i18n.getResource("en", "help", "gettingAroundWhereMessagesAppear"));
    }
  });

  it("explains the per-account sign-in lock as temporary, without a non-existent admin reset", () => {
    render(<HelpPage />);
    const signIn = screen.getByRole("heading", { name: "Signing in", level: 3 });
    // the lock is described as temporary (wait it out) — the app has no admin
    // password-reset/unlock action, and a reset wouldn't clear the lock anyway.
    expect(screen.getByText(/too many wrong passwords for/i)).toBeInTheDocument();
    expect(screen.getByText(/wait up to about 15 minutes/i)).toBeInTheDocument();
    expect(screen.queryByText(/administrator to set a new password/i)).not.toBeInTheDocument();
    // #145 — the session-persistence + post-update re-login note.
    expect(screen.getByText(/kept in your browser securely/i)).toBeInTheDocument();
    // #169 — the session survives the app being open in several tabs at once.
    expect(screen.getByText(/several tabs/i)).toBeInTheDocument();
    expect(signIn).toBeInTheDocument();
  });

  it("distinguishes self-change session retention from an admin reset in every catalog", () => {
    render(<HelpPage />);
    expect(screen.getByText(/keeps this device signed in/i)).toBeInTheDocument();
    expect(screen.getByText(/every one of your open sessions ends on its next request/i)).toBeInTheDocument();

    expect(en.help.ownPassword).toMatch(
      /keeps this device signed in.*every <em>other<\/em> open session.*next request/i,
    );
    expect(en.help.ownPassword).toMatch(
      /admin sets your password.*every one of your open sessions ends on its next request/i,
    );
    expect(es.help.ownPassword).toMatch(
      /mantiene este dispositivo conectado.*cada <em>otra<\/em> sesión abierta.*siguiente solicitud/i,
    );
    expect(es.help.ownPassword).toMatch(
      /administrador le establece la contraseña.*todas sus sesiones abiertas terminan.*siguiente solicitud/i,
    );
    expect(tl.help.ownPassword).toMatch(
      /naka-sign in sa device na ito.*bawat <em>ibang<\/em> bukas na session.*susunod nitong request/i,
    );
    expect(tl.help.ownPassword).toMatch(
      /admin ang magse-set ng password mo.*lahat ng bukas mong session.*susunod nitong request/i,
    );
    for (const catalog of [en, es, tl])
      expect(catalog.help.ownPassword).not.toMatch(/few minutes|unos minutos|ilang minuto/i);
  });

  it("documents the per-account report throttle a user can actually hit (#311)", () => {
    // #311 caps concurrently in-flight reports per account, so a real user can
    // meet a 429 on the Reports screen. Both in-app surfaces must say so — the
    // Reports section (what to do when it happens) and the glossary (the term
    // itself) — or the behavior is documented only in the product glossary,
    // which nobody using the app ever reads.
    render(<HelpPage />);
    expect(screen.getByText(/the farm runs only a few reports at a time/i)).toBeInTheDocument();
    // The reassurance is the load-bearing half: a refused report must not read
    // as lost work. Dropping it would leave a user re-entering a range they
    // never lost.
    expect(screen.getByText(/Nothing was recorded and nothing was lost/i)).toBeInTheDocument();
    expect(screen.getByText("Too many reports at once", { selector: "dt a" })).toBeInTheDocument();
  });

  it("renders the report-throttle bullet through <Trans>, so its <strong> tags are real elements", () => {
    // The copy leans on <strong> to surface the exact phrase a user sees in
    // the error ("try again shortly"). A regression from <Trans> back to a
    // plain {t(...)} call would render "<strong>" as inert literal text — this
    // asserts a real STRONG element instead, which that regression fails.
    const { container } = render(<HelpPage />);
    const strongs = Array.from(container.querySelectorAll("strong")).map((s) => s.textContent);
    expect(strongs).toContain("If a report is refused");
    expect(strongs).toContain("try again shortly");
    expect(screen.queryByText(/<strong>/)).not.toBeInTheDocument();
  });

  it("explains that grade reconciliation must be exact, not just not-over, on submit and admin adjust (#394)", () => {
    render(<HelpPage />);

    // dailyEntryPanes: the sellable figure is introduced, then the draft/
    // official distinction is explicit — a draft may fall short, submitting
    // may not. The old "can never exceed it" half (over-only) is gone.
    expect(
      screen.getByText(/A draft can leave that partly done, or not started at all/),
    ).toBeInTheDocument();
    expect(screen.queryByText(/can never exceed it/i)).not.toBeInTheDocument();

    // dailyEntryGradingDown: the old copy only warned about overshooting
    // ("You cannot submit while it is over") — a worker must see that being
    // short is refused exactly the same way, down to reading zero.
    expect(screen.getByText(/You cannot submit until it reads exactly zero/)).toBeInTheDocument();
    expect(
      screen.getByText(/grading a day partway, or not at all, is fine for a draft but not for Submit/),
    ).toBeInTheDocument();
    expect(screen.queryByText(/You cannot submit while it is over/)).not.toBeInTheDocument();

    // mistakesRow8Fix (History → adjust): the corrected grades must reconcile
    // exactly and Save adjustment is blocked until they do — while the
    // pre-existing sold-eggs floor and previous-values snapshot still hold.
    // Scoped to this row's cell: the "must add up to ... exactly" clause is
    // deliberately echoed in glossaryAdjustEntryDef too (same fact, two
    // places), which an unscoped getByText would match twice.
    // The fix is a <dd> whose text (partly in <strong>) carries the phrase.
    const adjustFixCell = screen.getByText((_, el) => el?.tagName === "DD" && /Save adjustment/.test(el.textContent ?? ""));
    expect(
      within(adjustFixCell).getByText(/The corrected grades must add up to the corrected sellable count exactly/),
    ).toBeInTheDocument();
    expect(within(adjustFixCell).getByText(/is blocked until they do/)).toBeInTheDocument();
    expect(within(adjustFixCell).getByText(/shrinking a grade below what was sold is refused/)).toBeInTheDocument();
    expect(within(adjustFixCell).getByText(/The previous values stay visible on the entry/)).toBeInTheDocument();
  });

  it("documents disabling and re-enabling a user, immediately and without deleting old sessions (#356)", () => {
    render(<HelpPage />);
    expect(screen.getByRole("heading", { name: "Who can do what", level: 3 })).toBeInTheDocument();
    // Takes effect immediately — on the very next request, not at token expiry.
    // ("...ends on its very next request" also appears in the glossary row
    // below, so this asserts the phrase unique to the roles bullet.)
    expect(screen.getByText(/cuts off access immediately/i)).toBeInTheDocument();
    expect(screen.getByText(/the same as a role change or password reset/i)).toBeInTheDocument();
    // A reason is optional, and recorded either way.
    // ("lands in the audit log" also appears in the Audit log section below,
    // so this asserts the fuller phrase unique to the roles bullet.)
    expect(screen.getByText(/A reason is optional/i)).toBeInTheDocument();
    expect(screen.getByText(/either way it lands in the audit log/i)).toBeInTheDocument();
    // Re-enabling restores sign-in but not the old sessions.
    expect(screen.getByText(/never revives the sessions the disable ended/i)).toBeInTheDocument();
    expect(screen.getByText(/sign in fresh with their existing password/i)).toBeInTheDocument();
    // Self-target and last-Owner refusals.
    expect(screen.getByText(/can't disable your own sign-in/i)).toBeInTheDocument();
    expect(screen.getByText(/can't disable the account's last Admin \(owner\)/i)).toBeInTheDocument();
  });

  // Every SYSTEM actor an owner can meet in the audit log on a real farm.
  it("explains the bracketed system actors in the audit log", () => {
    render(<HelpPage />);
    expect(screen.getByText(/\(bootstrap-admin\)/)).toBeInTheDocument();
    expect(screen.getByText(/\(break-glass\)/)).toBeInTheDocument();
    expect(screen.getByText(/\(suspend-account\)/)).toBeInTheDocument();
    expect(screen.getByText(/\(reactivate-account\)/)).toBeInTheDocument();
    expect(screen.getByText(/\(provision-account\)/)).toBeInTheDocument();
    // The accountability half matters as much as the label: a break-glass
    // reset records the machine and the reason, so it is never anonymous.
    expect(screen.getByText(/which machine it was run from and the reason given/i)).toBeInTheDocument();
    // And it must not leave the reader thinking everything is nameless.
    expect(screen.getByText(/Everything else names the person who did it/i)).toBeInTheDocument();
  });

  it("ships the provision-account system actor in every help catalog", () => {
    for (const catalog of [en, es, tl])
      expect(catalog.help.auditSystemActors).toContain("(provision-account)");
  });

  it("documents farm provisioning in the in-app glossary (#533)", () => {
    const originalTerm = i18n.getResource("en", "help", "glossaryFarmProvisioningTerm") as string;
    const originalDef = i18n.getResource("en", "help", "glossaryFarmProvisioningDef") as string;
    i18n.addResource("en", "help", "glossaryFarmProvisioningTerm", "PROVISIONING-TERM-MARKER");
    i18n.addResource("en", "help", "glossaryFarmProvisioningDef", "PROVISIONING-DEF-MARKER");
    try {
      render(<HelpPage />);
      expect(screen.getByText("PROVISIONING-TERM-MARKER", { selector: "dt a" })).toBeInTheDocument();
      expect(screen.getByText("PROVISIONING-DEF-MARKER")).toBeInTheDocument();
    } finally {
      i18n.addResource("en", "help", "glossaryFarmProvisioningTerm", originalTerm);
      i18n.addResource("en", "help", "glossaryFarmProvisioningDef", originalDef);
    }

    for (const catalog of [es, tl]) {
      expect(catalog.help.glossaryFarmProvisioningTerm).toBeTruthy();
      expect(catalog.help.glossaryFarmProvisioningTerm).not.toBe(en.help.glossaryFarmProvisioningTerm);
      expect(catalog.help.glossaryFarmProvisioningDef).toBeTruthy();
      expect(catalog.help.glossaryFarmProvisioningDef).not.toBe(en.help.glossaryFarmProvisioningDef);
    }
    expect(en.help.glossaryFarmProvisioningDef).toContain("UTC");
    expect(en.help.glossaryFarmProvisioningDef).toContain("Settings");
    expect(es.help.glossaryFarmProvisioningDef).toContain("UTC");
    expect(es.help.glossaryFarmProvisioningDef).toContain("Configuración");
    expect(tl.help.glossaryFarmProvisioningDef).toContain("UTC");
    expect(tl.help.glossaryFarmProvisioningDef).toContain("Settings");
  });

  it("enumerates every step-up-gated category and the explicit ungated boundary (#356, #360)", () => {
    // Pin the semantics, not merely the count: a stale Owner-only description
    // can still say "Six" while omitting one of the widened categories.
    render(<HelpPage />);
    const paragraph = screen.getByText(/Eight actions on the/i).closest("li")!;
    expect(paragraph).toHaveTextContent(/creating any user/i);
    expect(paragraph).toHaveTextContent(/resetting any user's password/i);
    expect(paragraph).toHaveTextContent(/changing any user's role/i);
    expect(paragraph).toHaveTextContent(/changing a login email/i);
    expect(paragraph).toHaveTextContent(/disabling a user/i);
    expect(paragraph).toHaveTextContent(/re-enabling a user/i);
    expect(paragraph).toHaveTextContent(/assigning a worker to a flock/i);
    expect(paragraph).toHaveTextContent(/removing a worker's flock assignment/i);
    // #606 — flock-assignment changes are no longer ungated; only display
    // name is left as the explicit contrast.
    expect(paragraph).toHaveTextContent(/Display-name changes do not ask again/i);
    expect(paragraph).not.toHaveTextContent(/flock-assignment changes do not ask again/i);

    expect(en.help.rolesAdmin).toContain(
      "Creating any sign-in, resetting any user's password, and changing any user's role asks the signed-in Admin (owner) to re-enter their current password.",
    );
    expect(en.help.rolesAdmin).not.toContain("every other role change needs no re-confirmation");
    expect(es.help.rolesAdmin).toContain(
      "Crear cualquier inicio de sesión, restablecer la contraseña de cualquier usuario y cambiar el rol de cualquier usuario pide al Administrador (propietario) que ha iniciado sesión que vuelva a ingresar su contraseña actual.",
    );
    expect(es.help.rolesAdmin).not.toContain("cualquier otro cambio de rol no necesita reconfirmación");
    expect(tl.help.rolesAdmin).toContain(
      "Ang paggawa ng kahit anong sign-in, pag-reset ng password ng kahit sinong user, at pagpapalit ng tungkulin ng kahit sinong user ay humihiling sa naka-sign-in na Admin (may-ari) na muling ilagay ang kasalukuyan niyang password.",
    );
    expect(tl.help.rolesAdmin).not.toContain("hindi na kailangan ng reconfirmation ang ibang pagbabago ng tungkulin");
  });

  it("documents the Disabled user term in the in-app glossary (#356)", () => {
    render(<HelpPage />);
    expect(screen.getByText("Disabled user", { selector: "dt a" })).toBeInTheDocument();
    expect(screen.getByText(/Revoked access, not deletion/i)).toBeInTheDocument();
  });

  it("ships es/tl for the disable-user roles bullet and glossary row, not English placeholders (#356)", () => {
    for (const catalog of [es, tl]) {
      expect(catalog.help.rolesDisableUser).toBeTruthy();
      expect(catalog.help.rolesDisableUser).not.toBe(en.help.rolesDisableUser);
      expect(catalog.help.glossaryDisabledUserTerm).toBeTruthy();
      expect(catalog.help.glossaryDisabledUserTerm).not.toBe(en.help.glossaryDisabledUserTerm);
      expect(catalog.help.glossaryDisabledUserDef).toBeTruthy();
      expect(catalog.help.glossaryDisabledUserDef).not.toBe(en.help.glossaryDisabledUserDef);
    }
  });

  // #625 — customer editing shipped: the Help page must say details can be
  // changed after creation, in every catalog.
  it("documents that a customer's details can be edited after creation (#625)", () => {
    render(<HelpPage />);
    expect(screen.getByText(/A customer's details.*can be edited at any time/i)).toBeInTheDocument();

    for (const catalog of [es, tl]) {
      expect(catalog.help.salesCustomerEdit).toBeTruthy();
      expect(catalog.help.salesCustomerEdit).not.toBe(en.help.salesCustomerEdit);
    }
  });

  it("scroll-spies the contents rail — the section in view is marked current", () => {
    render(<HelpPage />);
    const toc = screen.getByRole("navigation", { name: "Help contents" });

    // first item is current by default (Getting around leads the guide now)
    expect(within(toc).getByRole("link", { name: "Getting around" })).toHaveAttribute("aria-current", "location");

    // 'Flocks & birds' scrolls into view → it becomes current, the previous clears
    act(() => ioCallback?.([{ isIntersecting: true, target: { id: "flocks" }, boundingClientRect: { top: 12 } }]));
    const flocks = within(toc).getByRole("link", { name: "Flocks & birds" });
    expect(flocks).toHaveClass("active");
    expect(flocks).toHaveAttribute("aria-current", "location");
    expect(within(toc).getByRole("link", { name: "Getting around" })).not.toHaveAttribute("aria-current");
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 32, batch B6a)
// ---------------------------------------------------------------------------

// `help` IS in TRANSLATED_NAMESPACES, but these tests still run under the
// default English locale, so asserting the plain English string would prove
// nothing beyond "the fallback still works" (the same CONTRIBUTING-i18n.md
// fallback trap the other batches' i18n-wiring suites guard against). Swap
// the catalog value at runtime instead — the same i18n.addResource technique
// used by AccountPage.test.tsx/SettingsPage.test.tsx — so each marker only
// renders if the component actually reads the catalog rather than a literal
// that happens to still match it. A hardcoded literal instead of a t()/
// <Trans> call fails these assertions: that IS the mutation probe.
describe("HelpPage i18n wiring (#182, Task 32)", () => {
  function withOverride(key: string, value: string, run: () => void) {
    const original = i18n.getResource("en", "help", key) as string;
    i18n.addResource("en", "help", key, value);
    try {
      run();
    } finally {
      i18n.addResource("en", "help", key, original);
    }
  }

  it("reads the page heading from the catalog, not a hardcoded literal", () => {
    withOverride("heading", "HEADING-MARKER", () => {
      render(<HelpPage />);
      expect(screen.getByRole("heading", { name: "HEADING-MARKER", level: 2 })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Help", level: 2 })).not.toBeInTheDocument();
    });
  });

  it("reads the lead paragraph from the catalog, not a hardcoded literal", () => {
    withOverride("lead", "LEAD-MARKER", () => {
      render(<HelpPage />);
      expect(screen.getByText("LEAD-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/how Cluckwork works/i)).not.toBeInTheDocument();
    });
  });

  it("reads a contents-rail label from the catalog via its toc* key, not a hardcoded literal", () => {
    withOverride("tocDailyLoop", "RAIL-MARKER", () => {
      render(<HelpPage />);
      const toc = screen.getByRole("navigation", { name: "Help contents" });
      expect(within(toc).getByRole("link", { name: "RAIL-MARKER" })).toHaveAttribute("href", "#daily-loop");
      expect(within(toc).queryByRole("link", { name: "The daily loop" })).not.toBeInTheDocument();
    });
  });

  it("reads a plain (no-<Trans>) list item from the catalog via t(), not a hardcoded literal", () => {
    withOverride("flocksPermissions", "PLAIN-ITEM-MARKER", () => {
      render(<HelpPage />);
      expect(screen.getByText("PLAIN-ITEM-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/create a flock and view the bird ledger/i)).not.toBeInTheDocument();
    });
  });

  it("reads the Page loading note from the catalog", () => {
    withOverride("gettingAroundPageLoading", "PAGE-LOADING-NOTE-MARKER", () => {
      render(<HelpPage />);
      expect(screen.getByText("PAGE-LOADING-NOTE-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/first time you open a screen after starting or updating Cluckwork/i))
        .not.toBeInTheDocument();
    });
  });

  it("reads a 'Fixing mistakes' table cell from the catalog, not a hardcoded literal", () => {
    withOverride("mistakesRow1Mistake", "MISTAKE-ROW-MARKER", () => {
      render(<HelpPage />);
      expect(screen.getByText("MISTAKE-ROW-MARKER", { selector: "dt" })).toBeInTheDocument();
      expect(screen.queryByText(/depleted or archived the wrong flock/i)).not.toBeInTheDocument();
    });
  });

  it("reads the report-throttle bullet from the catalog, not a hardcoded literal", () => {
    withOverride("reportsThrottle", "THROTTLE-MARKER", () => {
      render(<HelpPage />);
      expect(screen.getByText("THROTTLE-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/the farm runs only a few reports at a time/i)).not.toBeInTheDocument();
    });
  });

  // #356 — the disable/enable roles bullet, same shape as the other
  // <Trans>-rendered bullets above.
  it("reads the disable-user roles bullet from the catalog, not a hardcoded literal", () => {
    withOverride("rolesDisableUser", "DISABLE-USER-MARKER", () => {
      render(<HelpPage />);
      expect(screen.getByText("DISABLE-USER-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/cuts off access immediately/i)).not.toBeInTheDocument();
    });
  });

  // The multi-tag <Trans> proof: override a key whose en value carries BOTH
  // a <strong> and an <em> tag (signingInRateLimit) with a marker of the same
  // shape, and assert real STRONG/EM elements come out the other end — not
  // literal "<strong>"/"<em>" text. This is what would catch a regression
  // from <Trans components={{ strong: <strong/>, em: <em/> }}> back to a
  // plain {t(...)} call (which would render the tags as inert text) or to a
  // components map missing one of the two tags (which would render that
  // tag's content as unwrapped plain text instead of an element).
  it("renders a multi-tag paragraph's <strong> and <em> as real DOM elements via <Trans>", () => {
    withOverride(
      "signingInRateLimit",
      "PRE-TEXT <strong>STRONG-MARK</strong> MID-TEXT <em>EM-MARK</em> POST-TEXT",
      () => {
        render(<HelpPage />);
        const strong = screen.getByText("STRONG-MARK");
        expect(strong.tagName).toBe("STRONG");
        const em = screen.getByText("EM-MARK");
        expect(em.tagName).toBe("EM");
        // The surrounding plain text is still present, not swallowed —
        // proves the whole marker string round-tripped through the catalog.
        expect(screen.getByText(/PRE-TEXT/)).toBeInTheDocument();
        expect(screen.getByText(/MID-TEXT/)).toBeInTheDocument();
        expect(screen.getByText(/POST-TEXT/)).toBeInTheDocument();
        expect(screen.queryByText(/slow down anyone guessing passwords/i)).not.toBeInTheDocument();
      },
    );
  });
});

// ---------------------------------------------------------------------------
// Glossary i18n wiring (#182, Task 33, batch B6b)
// ---------------------------------------------------------------------------

// Same withOverride technique as the B6a suite above, redefined locally so
// this describe block stands alone — the glossary (h3 id="glossary" through
// the closing repo-note) is externalized here, after the rest of the page's
// prose was already done in 6a.
describe("HelpPage glossary i18n wiring (#182, Task 33)", () => {
  function withOverride(key: string, value: string, run: () => void) {
    const original = i18n.getResource("en", "help", key) as string;
    i18n.addResource("en", "help", key, value);
    try {
      run();
    } finally {
      i18n.addResource("en", "help", key, original);
    }
  }

  it("reads the glossary heading from the catalog, not a hardcoded literal, and keeps id=\"glossary\"", () => {
    withOverride("glossaryHeading", "GLOSSARY-HEADING-MARKER", () => {
      const { container } = render(<HelpPage />);
      expect(screen.getByRole("heading", { name: "GLOSSARY-HEADING-MARKER", level: 3 })).toBeInTheDocument();
      expect(screen.queryByRole("heading", { name: "Glossary", level: 3 })).not.toBeInTheDocument();
      // the scroll-spy anchor id is untouched by the text-content change
      expect(container.querySelector("h3#glossary")).toHaveTextContent("GLOSSARY-HEADING-MARKER");
    });
  });

  it("reads a glossary row's term and definition from the catalog, not a hardcoded literal", () => {
    withOverride("glossaryFifoTerm", "FIFO-TERM-MARKER", () => {
      withOverride("glossaryFifoDef", "FIFO-DEF-MARKER", () => {
        render(<HelpPage />);
        expect(screen.getByText("FIFO-TERM-MARKER", { selector: "dt a" })).toBeInTheDocument();
        expect(screen.getByText("FIFO-DEF-MARKER")).toBeInTheDocument();
        expect(screen.queryByText("FIFO", { selector: "dt a" })).not.toBeInTheDocument();
        expect(screen.queryByText(/first in, first out/i)).not.toBeInTheDocument();
      });
    });
  });

  it("reads the Page loading glossary row from the catalog", () => {
    withOverride("glossaryPageLoadingTerm", "PAGE-LOADING-TERM-MARKER", () => {
      withOverride("glossaryPageLoadingDef", "PAGE-LOADING-DEF-MARKER", () => {
        render(<HelpPage />);
        expect(screen.getByText("PAGE-LOADING-TERM-MARKER", { selector: "dt a" })).toBeInTheDocument();
        expect(screen.getByText("PAGE-LOADING-DEF-MARKER")).toBeInTheDocument();
      });
    });
  });

  it("renders the flock-scoping glossary term and definition from the catalog (#388)", () => {
    withOverride("glossaryFlockScopingTerm", "FLOCK-SCOPE-TERM-MARKER", () => {
      withOverride("glossaryFlockScopingDef", "FLOCK-SCOPE-DEF-MARKER", () => {
        render(<HelpPage />);
        expect(screen.getByText("FLOCK-SCOPE-TERM-MARKER", { selector: "dt a" })).toBeInTheDocument();
        expect(screen.getByText("FLOCK-SCOPE-DEF-MARKER")).toBeInTheDocument();
        expect(screen.queryByText("Flock scoping", { selector: "dt a" })).not.toBeInTheDocument();
      });
    });
  });

  it("reads the report-throttle glossary row from the catalog, not a hardcoded literal", () => {
    withOverride("glossaryTooManyReportsTerm", "REPORT-THROTTLE-TERM-MARKER", () => {
      withOverride("glossaryTooManyReportsDef", "REPORT-THROTTLE-DEF-MARKER", () => {
        render(<HelpPage />);
        expect(screen.getByText("REPORT-THROTTLE-TERM-MARKER", { selector: "dt a" })).toBeInTheDocument();
        expect(screen.getByText("REPORT-THROTTLE-DEF-MARKER")).toBeInTheDocument();
        expect(screen.queryByText("Too many reports at once", { selector: "dt a" })).not.toBeInTheDocument();
      });
    });
  });

  // #612 — the Worker sale allocation glossary row, same shape as FIFO above.
  it("reads the worker sale allocation glossary row's term and definition from the catalog, not a hardcoded literal", () => {
    withOverride("glossaryWorkerSaleAllocationTerm", "WORKER-SALE-ALLOCATION-TERM-MARKER", () => {
      withOverride("glossaryWorkerSaleAllocationDef", "WORKER-SALE-ALLOCATION-DEF-MARKER", () => {
        render(<HelpPage />);
        expect(screen.getByText("WORKER-SALE-ALLOCATION-TERM-MARKER", { selector: "dt a" })).toBeInTheDocument();
        expect(screen.getByText("WORKER-SALE-ALLOCATION-DEF-MARKER")).toBeInTheDocument();
        expect(screen.queryByText("Worker sale allocation", { selector: "dt a" })).not.toBeInTheDocument();
      });
    });
  });

  // #356 — the Disabled user row, appended last in the table (same shape as
  // the FIFO/step-up rows above).
  it("reads the Disabled user glossary row's term and definition from the catalog, not a hardcoded literal", () => {
    withOverride("glossaryDisabledUserTerm", "DISABLED-USER-TERM-MARKER", () => {
      withOverride("glossaryDisabledUserDef", "DISABLED-USER-DEF-MARKER", () => {
        render(<HelpPage />);
        expect(screen.getByText("DISABLED-USER-TERM-MARKER", { selector: "dt a" })).toBeInTheDocument();
        expect(screen.getByText("DISABLED-USER-DEF-MARKER")).toBeInTheDocument();
        expect(screen.queryByText("Disabled user", { selector: "dt a" })).not.toBeInTheDocument();
      });
    });
  });

  // #308 — step-up authentication's in-app glossary row. Same shape as the
  // FIFO row above: proves the row exists and reads term + def from the
  // catalog, not a hardcoded literal.
  it("reads the step-up authentication row's term and definition from the catalog, not a hardcoded literal", () => {
    withOverride("glossaryStepUpAuthTerm", "STEP-UP-TERM-MARKER", () => {
      withOverride("glossaryStepUpAuthDef", "STEP-UP-DEF-MARKER", () => {
        render(<HelpPage />);
        expect(screen.getByText("STEP-UP-TERM-MARKER", { selector: "dt a" })).toBeInTheDocument();
        expect(screen.getByText("STEP-UP-DEF-MARKER")).toBeInTheDocument();
        expect(screen.queryByText("Step-up authentication", { selector: "dt a" })).not.toBeInTheDocument();
        // "re-enter your current password" also appears in the unrelated Signing-in
        // prose (signingInStepUp) — assert against phrasing unique to the glossary
        // def so this doesn't false-pass against that other section.
        expect(screen.queryByText(/extra check on top of being signed in/i)).not.toBeInTheDocument();
      });
    });
  });

  // The Farm palette row's <td> is faithfully plain prose (no interleaved
  // JSX) — confirm it also reads from the catalog via t(), not a literal.
  it("reads the Farm palette row's definition from the catalog, not a hardcoded literal", () => {
    withOverride("glossaryFarmPaletteDef", "FARM-PALETTE-DEF-MARKER", () => {
      render(<HelpPage />);
      expect(screen.getByText("FARM-PALETTE-DEF-MARKER")).toBeInTheDocument();
      expect(screen.queryByText(/farm-wide accent colour/i)).not.toBeInTheDocument();
    });
  });

  // The one <Trans>-in-a-row case: Install to home screen's definition
  // carries a single <strong>not</strong>. Overriding with a marker of the
  // same shape and asserting a real STRONG element comes out is what would
  // catch a regression to a plain {t(...)} call (tags render as inert text)
  // or a components map missing "strong" (content renders unwrapped).
  it("renders the Install-to-home-screen row's <strong> as a real DOM element via <Trans>", () => {
    withOverride(
      "glossaryInstallToHomeScreenDef",
      "PRE-TEXT <strong>STRONG-MARK</strong> POST-TEXT",
      () => {
        render(<HelpPage />);
        const strong = screen.getByText("STRONG-MARK");
        expect(strong.tagName).toBe("STRONG");
        expect(screen.getByText(/PRE-TEXT/)).toBeInTheDocument();
        expect(screen.getByText(/POST-TEXT/)).toBeInTheDocument();
        expect(screen.queryByText(/does not make the app work offline/i)).not.toBeInTheDocument();
      },
    );
  });

  // The closing repo-note's <Trans> renders a <code> element around the
  // (untranslated, literal) GLOSSARY.md path. Confirm both that the CODE
  // element is real DOM (not inert "<code>" text) and that overriding the
  // surrounding sentence proves the whole string is catalog-driven.
  it("renders the closing note's <code> path as a real DOM element via <Trans>, from the catalog", () => {
    withOverride(
      "glossaryRepoNote",
      "REPO-NOTE-MARKER <code>specs/product/GLOSSARY.md</code>.",
      () => {
        render(<HelpPage />);
        const code = screen.getByText("specs/product/GLOSSARY.md");
        expect(code.tagName).toBe("CODE");
        expect(screen.getByText(/REPO-NOTE-MARKER/)).toBeInTheDocument();
        expect(screen.queryByText(/Full spec-language definitions live in the repository's/i))
          .not.toBeInTheDocument();
      },
    );
  });

  // #398 — the SPA now refuses a fractional sale-line quantity before sending.
  // The stepper note used to end "Prices and fractional amounts are still
  // typed", which reads as permission to TYPE a fractional quantity — the exact
  // thing that is now an error. The assertion has to be about that sentence
  // saying quantities are whole numbers, not merely that the section renders:
  // the section rendered perfectly happily with the misleading copy in it.
  it("tells the reader a sale quantity is a whole number, not that fractional amounts are typeable (#398)", () => {
    render(<HelpPage />);
    expect(screen.getByText(/always a/i)).toBeInTheDocument();
    expect(screen.getByText(/stepped or typed/i)).toBeInTheDocument();
    expect(screen.getByText(/Decimals belong in prices/i)).toBeInTheDocument();
    // The retired wording must be gone, not merely joined by the new sentence —
    // leaving both in place would still tell a worker fractional amounts are
    // acceptable input for a quantity.
    expect(screen.queryByText(/Prices and fractional amounts are still typed/i))
      .not.toBeInTheDocument();
  });

  it("documents login-email replacement through the localized glossary row (#357)", () => {
    const originalTerm = i18n.getResource("en", "help", "glossaryLoginEmailTerm") as string;
    const originalDef = i18n.getResource("en", "help", "glossaryLoginEmailDef") as string;
    i18n.addResource("en", "help", "glossaryLoginEmailTerm", "LOGIN-EMAIL-TERM-MARKER");
    i18n.addResource("en", "help", "glossaryLoginEmailDef", "LOGIN-EMAIL-DEF-MARKER");
    try {
      render(<HelpPage />);
      expect(screen.getByText("LOGIN-EMAIL-TERM-MARKER", { selector: "dt a" })).toBeInTheDocument();
      expect(screen.getByText("LOGIN-EMAIL-DEF-MARKER")).toBeInTheDocument();
    } finally {
      i18n.addResource("en", "help", "glossaryLoginEmailTerm", originalTerm);
      i18n.addResource("en", "help", "glossaryLoginEmailDef", originalDef);
    }

    expect(en.help.glossaryLoginEmailDef).toMatch(/no confirmation email/i);
    expect(es.help.glossaryLoginEmailDef).toMatch(/no se envía.*confirmación/i);
    expect(tl.help.glossaryLoginEmailDef).toMatch(/walang.*confirmation email/i);
    for (const catalog of [es, tl]) {
      expect(catalog.help.glossaryLoginEmailTerm).not.toBe(en.help.glossaryLoginEmailTerm);
      expect(catalog.help.glossaryLoginEmailDef).not.toBe(en.help.glossaryLoginEmailDef);
    }
  });

  it("explains the Owner change-email flow in Who can do what (#357)", () => {
    render(<HelpPage />);
    const guidance = screen.getByText((_, element) =>
      element?.tagName === "LI"
      && /Owners can replace a user's login email immediately/i.test(element.textContent ?? ""));
    expect(guidance).toHaveTextContent(/no confirmation email is sent/i);
  });
});

// ---------------------------------------------------------------------------
// Searchable picker guidance (#512 T060/T061)
// ---------------------------------------------------------------------------

// FR-051: user help and the in-app glossary must explain searchable paged
// pickers, keyboard selection, Load more, Retry, and customer-name links into
// filtered Sales, in English, Spanish, and Tagalog.
describe("HelpPage searchable picker guidance (#512)", () => {
  it("explains searchable pickers in Getting around: search, exploration vs commit, keyboard, Load more, Retry, and unavailable identities", () => {
    render(<HelpPage />);
    const item = screen.getByText(/searchable pickers/i).closest("li")!;
    // Search + exploration-vs-commit.
    expect(item).toHaveTextContent(/type to search/i);
    expect(item).toHaveTextContent(/Enter/);
    expect(item).toHaveTextContent(/previous choice stays/i);
    expect(item).toHaveTextContent(/Escape/);
    // Load more / Retry — the picker's own catalog vocabulary (namedEntityPicker),
    // never a paraphrase, so the guidance can never drift from what the control
    // actually says.
    expect(item).toHaveTextContent("Load more");
    expect(item).toHaveTextContent("Retry");
    // Unavailable identities.
    expect(item).toHaveTextContent("Unavailable");

    for (const catalog of [es, tl]) {
      expect(catalog.help.gettingAroundSearchablePicker).toBeTruthy();
      expect(catalog.help.gettingAroundSearchablePicker).not.toBe(en.help.gettingAroundSearchablePicker);
    }
  });

  it("reads the searchable-picker guidance from the catalog, not a hardcoded literal", () => {
    const original = i18n.getResource("en", "help", "gettingAroundSearchablePicker") as string;
    i18n.addResource("en", "help", "gettingAroundSearchablePicker", "PICKER-GUIDANCE-MARKER");
    try {
      render(<HelpPage />);
      expect(screen.getByText(/PICKER-GUIDANCE-MARKER/)).toBeInTheDocument();
      expect(screen.queryByText(/searchable pickers/i)).not.toBeInTheDocument();
    } finally {
      i18n.addResource("en", "help", "gettingAroundSearchablePicker", original);
    }
  });

  it("documents customer-name links into filtered Sales, in every catalog (#512 US5)", () => {
    render(<HelpPage />);
    const item = screen.getByText(/link into|links into/i).closest("li")!;
    expect(item).toHaveTextContent(/Customers/);
    expect(item).toHaveTextContent(/Sales/);

    for (const catalog of [es, tl]) {
      expect(catalog.help.salesCustomerLink).toBeTruthy();
      expect(catalog.help.salesCustomerLink).not.toBe(en.help.salesCustomerLink);
    }
  });

  it("defines the Searchable picker term in the in-app glossary, covering keyboard, Load more, Retry, and unavailable identities", () => {
    render(<HelpPage />);
    expect(screen.getByText("Searchable picker", { selector: "dt a" })).toBeInTheDocument();
    const def = screen.getByText("Searchable picker", { selector: "dt a" }).closest(".glossary-entry")!;
    expect(def).toHaveTextContent(/Enter/);
    expect(def).toHaveTextContent(/Escape/);
    expect(def).toHaveTextContent("Load more");
    expect(def).toHaveTextContent("Retry");
    expect(def).toHaveTextContent("Unavailable");

    for (const catalog of [es, tl]) {
      expect(catalog.help.glossarySearchablePickerTerm).toBeTruthy();
      expect(catalog.help.glossarySearchablePickerTerm).not.toBe(en.help.glossarySearchablePickerTerm);
      expect(catalog.help.glossarySearchablePickerDef).toBeTruthy();
      expect(catalog.help.glossarySearchablePickerDef).not.toBe(en.help.glossarySearchablePickerDef);
    }
  });

  it("reads the Searchable picker glossary row from the catalog, not a hardcoded literal", () => {
    const originalTerm = i18n.getResource("en", "help", "glossarySearchablePickerTerm") as string;
    const originalDef = i18n.getResource("en", "help", "glossarySearchablePickerDef") as string;
    i18n.addResource("en", "help", "glossarySearchablePickerTerm", "SEARCHABLE-PICKER-TERM-MARKER");
    i18n.addResource("en", "help", "glossarySearchablePickerDef", "SEARCHABLE-PICKER-DEF-MARKER");
    try {
      render(<HelpPage />);
      expect(screen.getByText("SEARCHABLE-PICKER-TERM-MARKER", { selector: "dt a" })).toBeInTheDocument();
      expect(screen.getByText("SEARCHABLE-PICKER-DEF-MARKER")).toBeInTheDocument();
    } finally {
      i18n.addResource("en", "help", "glossarySearchablePickerTerm", originalTerm);
      i18n.addResource("en", "help", "glossarySearchablePickerDef", originalDef);
    }
  });

  // The picker's own strings (namedEntityPicker.loadMore/retry/unavailable) are
  // the load-bearing vocabulary: the Help guidance and glossary definition must
  // never paraphrase them into different English, or a user reading Help
  // wouldn't recognize the control they are looking at.
  it("uses the picker's own Load more, Retry, and Unavailable vocabulary, not a paraphrase", () => {
    expect(en.help.gettingAroundSearchablePicker).toContain(en.namedEntityPicker.loadMore);
    expect(en.help.gettingAroundSearchablePicker).toContain(en.namedEntityPicker.retry);
    expect(en.help.gettingAroundSearchablePicker).toContain(en.namedEntityPicker.unavailable);
    expect(en.help.glossarySearchablePickerDef).toContain(en.namedEntityPicker.loadMore);
    expect(en.help.glossarySearchablePickerDef).toContain(en.namedEntityPicker.retry);
    expect(en.help.glossarySearchablePickerDef).toContain(en.namedEntityPicker.unavailable);
  });
});

// #657 — grouped, searchable, deep-linkable glossary; guide reordered around
// the tasks people come for.
describe("HelpPage glossary + search (#657)", () => {
  it("renders the glossary as grouped definition lists, alphabetical within each group", () => {
    const { container } = render(<HelpPage />);
    const groups = Array.from(container.querySelectorAll(".glossary-group"));
    expect(groups.length).toBeGreaterThanOrEqual(5);
    let entries = 0;
    for (const g of groups) {
      expect(g.querySelector("h4")).not.toBeNull();
      const terms = Array.from(g.querySelectorAll("dl.glossary dt")).map((dt) => dt.textContent ?? "");
      expect(terms.length).toBeGreaterThan(0);
      expect(terms).toEqual([...terms].sort((a, b) => a.localeCompare(b, "en")));
      entries += terms.length;
    }
    expect(entries).toBe(GLOSSARY.length);
    expect(container.querySelector("table.data th[scope=\"row\"]")).toBeNull();
  });

  it("gives every term a stable anchor that links to itself", () => {
    const { container } = render(<HelpPage />);
    const entry = container.querySelector("#glossary-egg-lot");
    expect(entry).not.toBeNull();
    expect(entry!.querySelector("dt a")).toHaveAttribute("href", "#glossary-egg-lot");
    expect(entry!.querySelector("dt")).toHaveTextContent("Egg lot");
    for (const e of Array.from(container.querySelectorAll(".glossary-entry"))) {
      expect(e.id).toMatch(/^glossary-/);
      expect(e.querySelector("dt a")).toHaveAttribute("href", `#${e.id}`);
    }
  });

  it("puts Fixing mistakes straight after The daily loop", () => {
    const { container } = render(<HelpPage />);
    const ids = Array.from(container.querySelectorAll("h3[id]")).map((h) => h.id);
    expect(ids.indexOf("mistakes")).toBe(ids.indexOf("daily-loop") + 1);
  });

  it("groups the contents rail under labels, in the same order as the sections", () => {
    const { container } = render(<HelpPage />);
    const toc = screen.getByRole("navigation", { name: "Help contents" });
    expect(within(toc).getAllByText((_, el) => el?.classList.contains("help-toc-group") ?? false).length).toBeGreaterThanOrEqual(5);
    const linked = within(toc).getAllByRole("link").map((a) => a.getAttribute("href")?.slice(1));
    const sections = Array.from(container.querySelectorAll("h3[id]")).map((h) => h.id);
    expect(linked).toEqual(sections);
  });

  it("filters guide sections and glossary terms by the search text, and says how many matched", async () => {
    const user = userEvent.setup();
    const { container } = render(<HelpPage />);
    await user.type(screen.getByRole("searchbox", { name: "Search the guide" }), "fifo");

    expect(screen.getByText("FIFO", { selector: "dt a" })).toBeVisible();
    expect(container.querySelector("#glossary-ui-language")).not.toBeVisible();
    // hidden: true — a `hidden` section leaves the accessibility tree, which is the point.
    expect(screen.getByRole("heading", { name: "Install on a phone", level: 3, hidden: true })).not.toBeVisible();
    expect(screen.getByRole("status")).toHaveTextContent(/“fifo”/);
    expect(screen.getByRole("status")).toHaveTextContent(/2 in the glossary/); // FIFO itself + "Feed usage" (drains lots FIFO)

    await user.click(screen.getByRole("button", { name: "Clear search" }));
    expect(screen.getByRole("searchbox", { name: "Search the guide" })).toHaveValue("");
    expect(container.querySelector("#glossary-ui-language")).toBeVisible();
    expect(screen.getByRole("heading", { name: "Install on a phone", level: 3 })).toBeVisible();
    expect(screen.getByRole("status")).toHaveTextContent("");
  });

  it("says so when nothing matches", async () => {
    const user = userEvent.setup();
    render(<HelpPage />);
    await user.type(screen.getByRole("searchbox", { name: "Search the guide" }), "zzqxv");
    expect(screen.getByRole("status")).toHaveTextContent("Nothing matches “zzqxv”.");
    expect(screen.getByText("FIFO", { selector: "dt a" })).not.toBeVisible();
  });

  it("scrolls to the term named in the URL hash when the hash changes after mount", () => {
    const scroll = vi.fn();
    Element.prototype.scrollIntoView = scroll;
    window.location.hash = "";
    try {
      const { container } = render(<HelpPage />);
      expect(scroll).not.toHaveBeenCalled();
      window.location.hash = "#glossary-fifo";
      act(() => { window.dispatchEvent(new HashChangeEvent("hashchange")); });
      expect(scroll).toHaveBeenCalledTimes(1);
      expect(scroll.mock.instances[0]).toBe(container.querySelector("#glossary-fifo"));
    } finally {
      window.location.hash = "";
    }
  });

  it("follows a router navigation to a term's anchor (a GlossaryLink click)", async () => {
    const scroll = vi.fn();
    Element.prototype.scrollIntoView = scroll;
    const user = userEvent.setup();
    const { container } = render(
      <MemoryRouter initialEntries={["/help"]}>
        <GlossaryLink term="EggLot" />
        <HelpPage />
      </MemoryRouter>,
    );
    await user.click(screen.getByRole("link", { name: "What does “Egg lot” mean?" }));
    expect(scroll).toHaveBeenCalled();
    expect(scroll.mock.instances[scroll.mock.calls.length - 1]).toBe(container.querySelector("#glossary-egg-lot"));
  });

  it("clears an active search before following a hash, so the target is not hidden", async () => {
    const scroll = vi.fn();
    Element.prototype.scrollIntoView = scroll;
    const user = userEvent.setup();
    window.location.hash = "";
    try {
      const { container } = render(<HelpPage />);
      await user.type(screen.getByRole("searchbox", { name: "Search the guide" }), "fifo");
      expect(container.querySelector("#glossary-egg-lot")).not.toBeVisible();
      window.location.hash = "#glossary-egg-lot";
      act(() => { window.dispatchEvent(new HashChangeEvent("hashchange")); });
      expect(screen.getByRole("searchbox", { name: "Search the guide" })).toHaveValue("");
      expect(container.querySelector("#glossary-egg-lot")).toBeVisible();
      expect(scroll).toHaveBeenCalledTimes(1);
      expect(scroll.mock.instances[0]).toBe(container.querySelector("#glossary-egg-lot"));
    } finally {
      window.location.hash = "";
    }
  });

  it("ignores a malformed URL fragment instead of throwing", () => {
    const scroll = vi.fn();
    Element.prototype.scrollIntoView = scroll;
    window.location.hash = "#%E0%A4%A"; // truncated percent-encoding: decodeURIComponent throws URIError
    try {
      expect(() => render(<HelpPage />)).not.toThrow();
      expect(scroll).not.toHaveBeenCalled();
    } finally {
      window.location.hash = "";
    }
  });

  it("scrolls to the term named in the URL hash on mount", () => {
    const scroll = vi.fn();
    Element.prototype.scrollIntoView = scroll;
    window.location.hash = "#glossary-egg-lot";
    try {
      const { container } = render(<HelpPage />);
      expect(scroll).toHaveBeenCalledTimes(1);
      expect(scroll.mock.instances[0]).toBe(container.querySelector("#glossary-egg-lot"));
    } finally {
      window.location.hash = "";
    }
  });
});

// #657 visual pass — head band, glossary grid with a jump bar, section
// "Open <screen>" links, mistakes as a two-column list, "/" to search.
describe("HelpPage visual pass (#657)", () => {
  it("holds kicker, title, lead and the search in one head band", () => {
    const { container } = render(<HelpPage />);
    const hero = container.querySelector(".help-hero");
    expect(hero).not.toBeNull();
    expect(within(hero as HTMLElement).getByRole("heading", { name: "Help", level: 2 })).toBeInTheDocument();
    expect(within(hero as HTMLElement).getByRole("searchbox", { name: "Search the guide" })).toBeInTheDocument();
  });

  it("focuses the search on / unless the user is already typing somewhere", async () => {
    const user = userEvent.setup();
    render(<HelpPage />);
    const search = screen.getByRole("searchbox", { name: "Search the guide" });
    expect(search).not.toHaveFocus();
    await user.keyboard("/");
    expect(search).toHaveFocus();
    expect(search).toHaveValue(""); // the shortcut key itself is not typed
    await user.keyboard("fifo/");
    expect(search).toHaveValue("fifo/"); // inside the box, "/" is just a character
  });

  it("offers a jump bar to every glossary group", () => {
    const { container } = render(<HelpPage />);
    const jump = screen.getByRole("navigation", { name: "Glossary groups" });
    const hrefs = within(jump).getAllByRole("link").map((a) => a.getAttribute("href"));
    const groups = Array.from(container.querySelectorAll(".glossary-group h4")).map((h) => `#${h.id}`);
    expect(hrefs).toEqual(groups);
    expect(hrefs.length).toBe(7);
  });

  it("folds the glossary jump bar away while a search is active", async () => {
    const user = userEvent.setup();
    const { container } = render(<HelpPage />);
    expect(screen.getByRole("navigation", { name: "Glossary groups" })).toBeVisible();
    await user.type(screen.getByRole("searchbox", { name: "Search the guide" }), "fifo");
    // A hidden element has no accessible name (accname step 2A), so the folded
    // bar is reached by class rather than by role.
    expect(container.querySelector(".glossary-jump")).not.toBeVisible();
    await user.click(screen.getByRole("button", { name: "Clear search" }));
    expect(screen.getByRole("navigation", { name: "Glossary groups" })).toBeVisible();
  });

  it("renders Fixing mistakes as a mistake → fix list, not a table", () => {
    const { container } = render(<HelpPage />);
    const section = container.querySelector("#mistakes")!.closest("section")!;
    expect(section.querySelector("table")).toBeNull();
    const entries = section.querySelectorAll("dl.mistakes .mistake");
    expect(entries.length).toBe(10);
    for (const e of Array.from(entries)) {
      expect(e.querySelector("dt")).not.toBeNull();
      expect(e.querySelector("dd")).not.toBeNull();
    }
  });

  it("links each screen section to its screen, only for screens the role can reach", () => {
    renderWithProviders(<HelpPage />, { token: { sub: "u1", role: "Admin" } });
    expect(screen.getByRole("link", { name: "Open Stock" })).toHaveAttribute("href", "/stock");
    expect(screen.getByRole("link", { name: "Open Users" })).toHaveAttribute("href", "/users");
    expect(screen.queryByRole("link", { name: /^Open .*daily loop/i })).not.toBeInTheDocument();
  });

  it("hides a section's Open link from a role that cannot reach the screen", () => {
    renderWithProviders(<HelpPage />, { token: { sub: "u2" } }); // plain Worker
    expect(screen.getByRole("link", { name: "Open Daily entry" })).toHaveAttribute("href", "/daily-entry");
    expect(screen.queryByRole("link", { name: "Open Users" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Open Expenses" })).not.toBeInTheDocument(); // admin-only in nav.tsx
  });

  it("offers no Open links outside a session (bare render)", () => {
    render(<HelpPage />);
    expect(screen.queryByRole("link", { name: /^Open / })).not.toBeInTheDocument();
  });
});
