import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, within, act } from "@testing-library/react";
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
    expect(screen.getByRole("rowheader", { name: "FIFO" })).toBeInTheDocument();
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
    expect(screen.getByRole("rowheader", { name: "Farm settings" })).toBeInTheDocument();
    expect(screen.getByRole("rowheader", { name: "Currency lock" })).toBeInTheDocument();
    expect(screen.getByRole("rowheader", { name: "Farm logo" })).toBeInTheDocument();
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
    expect(screen.getByRole("rowheader", { name: "Too many reports at once" })).toBeInTheDocument();
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
    const adjustFixCell = screen.getByRole("cell", { name: /Save adjustment/ });
    expect(
      within(adjustFixCell).getByText(/The corrected grades must add up to the corrected sellable count exactly/),
    ).toBeInTheDocument();
    expect(within(adjustFixCell).getByText(/is blocked until they do/)).toBeInTheDocument();
    expect(within(adjustFixCell).getByText(/shrinking a grade below what was sold is refused/)).toBeInTheDocument();
    expect(within(adjustFixCell).getByText(/The previous values stay visible on the entry/)).toBeInTheDocument();
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

  it("reads a 'Fixing mistakes' table cell from the catalog, not a hardcoded literal", () => {
    withOverride("mistakesRow1Mistake", "MISTAKE-ROW-MARKER", () => {
      render(<HelpPage />);
      expect(screen.getByRole("cell", { name: "MISTAKE-ROW-MARKER" })).toBeInTheDocument();
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
        expect(screen.getByRole("rowheader", { name: "FIFO-TERM-MARKER" })).toBeInTheDocument();
        expect(screen.getByText("FIFO-DEF-MARKER")).toBeInTheDocument();
        expect(screen.queryByRole("rowheader", { name: "FIFO" })).not.toBeInTheDocument();
        expect(screen.queryByText(/first in, first out/i)).not.toBeInTheDocument();
      });
    });
  });

  it("reads the report-throttle glossary row from the catalog, not a hardcoded literal", () => {
    withOverride("glossaryTooManyReportsTerm", "REPORT-THROTTLE-TERM-MARKER", () => {
      withOverride("glossaryTooManyReportsDef", "REPORT-THROTTLE-DEF-MARKER", () => {
        render(<HelpPage />);
        expect(screen.getByRole("rowheader", { name: "REPORT-THROTTLE-TERM-MARKER" })).toBeInTheDocument();
        expect(screen.getByText("REPORT-THROTTLE-DEF-MARKER")).toBeInTheDocument();
        expect(screen.queryByRole("rowheader", { name: "Too many reports at once" })).not.toBeInTheDocument();
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
        expect(screen.getByRole("rowheader", { name: "STEP-UP-TERM-MARKER" })).toBeInTheDocument();
        expect(screen.getByText("STEP-UP-DEF-MARKER")).toBeInTheDocument();
        expect(screen.queryByRole("rowheader", { name: "Step-up authentication" })).not.toBeInTheDocument();
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
});
