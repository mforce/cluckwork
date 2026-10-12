import { useCallback, useContext, useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useInRouterContext, useLocation } from "react-router";
import { Search } from "lucide-react";
import { Container, Tooltip, Typography } from "@mui/material";
import { AuthContext } from "../auth/AuthContext";
import { navGroups } from "./nav";
import { HelpAdminSections } from "./help/HelpAdminSections";
import { HelpGlossarySection } from "./help/HelpGlossarySection";
import { HelpGuideSections } from "./help/HelpGuideSections";
import { HelpScreenSections } from "./help/HelpScreenSections";

// In-app user guide + glossary (#71): a docs layout with a sticky contents
// rail, one search box over everything, and a deep-linkable glossary whose
// data lives in ./helpGlossary.ts. KEEP THIS PAGE CURRENT: the docs-sync rule
// (AGENTS.md) requires every user-visible change to update the relevant
// section here and specs/product/GLOSSARY.md in the same PR.

// The contents rail mirrors the <h3 id=...> sections below in document order.
// The label (2nd element) is a `help` catalog key rendered through t() at
// render time. Do NOT rename an `id`: it drives both the <h3 id=...> anchors
// below and the scroll-spy IntersectionObserver further down.
const TOC = [
    ["getting-around", "tocGettingAround"],
    ["dashboard", "tocDashboard"],
    ["signing-in", "tocSigningIn"],
    ["daily-loop", "tocDailyLoop"],
    ["mistakes", "tocMistakes"],
    ["dialogs", "tocDialogs"],
    ["daily-entry", "tocDailyEntry"],
    ["flocks", "tocFlocks"],
    ["stock", "tocStock"],
    ["history", "tocHistory"],
    ["sales", "tocSales"],
    ["products", "tocProducts"],
    ["grades", "tocGrades"],
    ["reports", "tocReports"],
    ["expenses", "tocExpenses"],
    ["inventory", "tocInventory"],
    ["feed", "tocFeed"],
    ["water", "tocWater"],
    ["roles", "tocRoles"],
    ["farm-settings", "tocFarmSettings"],
    ["farm-palette", "tocFarmPalette"],
    ["account", "tocAccount"],
    ["audit", "tocAudit"],
    ["export", "tocExport"],
    ["install", "tocInstall"],
    ["glossary", "tocGlossary"],
] as const;

// #657 — the screen a guide section describes, so the section can offer
// "Open Stock". Keyed by section id; a section with no screen (the daily
// loop, fixing mistakes, install) has no entry. The link renders only for a
// route the signed-in role's own navigation carries (nav.tsx is the one
// place role gates live), so a Worker is never handed a door to a 403.
const ROUTE_FOR: Readonly<Record<string, string>> = {
  "dashboard": "/",
  "daily-entry": "/daily-entry",
  "flocks": "/flocks",
  "stock": "/stock",
  "history": "/history",
  "sales": "/sales",
  "products": "/products",
  "grades": "/grades",
  "reports": "/reports",
  "expenses": "/expenses",
  "inventory": "/inventory",
  "feed": "/feed",
  "water": "/water",
  "roles": "/users",
  "farm-settings": "/settings",
  "farm-palette": "/settings",
  "account": "/account",
  "audit": "/audit",
  "export": "/export"
};

// Rendered only inside a router (the tests mount HelpPage bare): follows the
// router's own location, which is what a <Link to="/help#…"> changes.
function RouterHashScroll({ onHash }: { onHash: (hash: string) => void }) {
  const { hash } = useLocation();
  useEffect(() => { onHash(hash); }, [hash, onHash]);
  return null;
}

export function HelpPage() {
  const { t, i18n } = useTranslation("help");
  const { t: tn } = useTranslation("nav");
  const searchId = useId();
  const searchShortcutHintId = `${searchId}-shortcut-hint`;
  const searchRef = useRef<HTMLInputElement>(null);
  const tocRef = useRef<HTMLElement>(null);

  // #657 — "Open <screen>" beside a section heading. Null outside a session
  // (the page is also reachable before sign-in in tests) and outside a router.
  const auth = useContext(AuthContext);
  const inRouter = useInRouterContext();
  const navEntries = auth === null ? [] : navGroups(auth.role, auth.isAdmin).flatMap((g) => g.entries);
  const openLink = (sectionId: string) => {
    const to = ROUTE_FOR[sectionId];
    if (to === undefined || !inRouter) return null;
    const entry = navEntries.find((e) => e.to === to);
    if (entry === undefined) return null;
    return (
      <Link className="help-open" to={to}>{t("openScreen", { screen: tn(entry.labelKey) })}</Link>
    );
  };

  // #657 — "/" jumps to the search from anywhere on the page, the way a docs
  // site does; not while the user is already typing in a field.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "/" || e.ctrlKey || e.metaKey || e.altKey) return;
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.tagName === "SELECT" || target.isContentEditable)) return;
      e.preventDefault();
      searchRef.current?.focus();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, []);

  // Scroll-spy the contents rail: highlight the section currently in view.
  const [activeId, setActiveId] = useState<string | null>(TOC[0][0]);
  const lastActiveId = useRef<string>(TOC[0][0]);
  const previousActiveId = useRef<string | null>(TOC[0][0]);
  const activate = useCallback((id: string) => {
    lastActiveId.current = id;
    setActiveId(id);
  }, []);

  useEffect(() => {
    if (activeId === previousActiveId.current) return;
    previousActiveId.current = activeId;
    if (activeId === null) return;
    tocRef.current?.querySelector<HTMLAnchorElement>("a.active")
      ?.scrollIntoView({ block: "nearest", inline: "nearest" });
  }, [activeId]);

  useEffect(() => {
    // jsdom (tests) has no IntersectionObserver — the rail still works as plain
    // anchors, it just doesn't auto-highlight there.
    if (typeof IntersectionObserver === "undefined") return;
    const observer = new IntersectionObserver(
      (entries) => {
        const inView = entries
          .filter((e) => e.isIntersecting)
          .sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top);
        if (inView[0]) activate(inView[0].target.id);
      },
      // "active" once a heading reaches the top ~30% of the viewport
      { rootMargin: "0px 0px -70% 0px", threshold: 0 },
    );
    for (const [id] of TOC) {
      const el = document.getElementById(id);
      if (el) observer.observe(el);
    }
    return () => observer.disconnect();
  }, [activate]);

  // #657 — a deep link (`/help#glossary-egg-lot`, from a GlossaryLink or a
  // pasted URL). The browser only scrolls to a fragment on a full load; a
  // client-side navigation lands at the top, so the target is scrolled to
  // here — on mount, and again whenever the fragment changes while the page
  // stays mounted (a GlossaryLink clicked from the Help page itself, or the
  // address bar edited). A router navigation is a pushState, which fires no
  // hashchange, so inside a router the location itself is watched too.
  //
  // A search in progress may be hiding the very entry the link names, so the
  // link clears the search first and scrolls once the target is back on
  // screen — that is what `pendingHash` waits for.
  const [query, setQuery] = useState("");
  const [pendingHash, setPendingHash] = useState<string | null>(null);
  const followHash = useCallback((hash: string) => {
    let id: string;
    try {
      id = decodeURIComponent(hash.slice(1));
    } catch {
      return; // a malformed fragment ("#%E0%A4%A") names nothing; ignore it
    }
    if (id === "") return;
    setQuery("");
    setPendingHash(id);
  }, []);
  useEffect(() => {
    const follow = () => followHash(window.location.hash);
    follow();
    window.addEventListener("hashchange", follow);
    return () => window.removeEventListener("hashchange", follow);
  }, [followHash]);
  useEffect(() => {
    if (pendingHash === null || query !== "") return;
    const target = document.getElementById(pendingHash);
    if (target instanceof HTMLDetailsElement) target.open = true;
    target?.scrollIntoView();
    setPendingHash(null);
  }, [pendingHash, query]);
  // #657 — one search over the guide sections and the glossary terms. The
  // sections are prose the catalog assembles at render time, so the match is
  // taken from the DOM text rather than from any list this page could keep:
  // every `[data-searchable]` element is hidden when its text does not carry
  // the query, and a glossary group folds when none of its terms survive.
  const [matches, setMatches] = useState<{ sections: number; terms: number } | null>(null);
  const bodyRef = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    const body = bodyRef.current;
    if (body === null) return;
    const q = query.trim().toLowerCase();
    let sections = 0;
    let terms = 0;
    for (const node of Array.from(body.querySelectorAll<HTMLElement>("[data-searchable]"))) {
      const hit = q === "" || (node.textContent ?? "").toLowerCase().includes(q);
      node.hidden = !hit;
      if (!hit) continue;
      if (node.dataset.searchable === "term") {
        terms += 1;
        if (q !== "" && node instanceof HTMLDetailsElement) node.open = true;
      }
      else if (node.dataset.searchable === "section") sections += 1;
    }
    for (const group of Array.from(body.querySelectorAll<HTMLElement>(".glossary-group"))) {
      group.hidden = q !== "" && group.querySelector(".glossary-entry:not([hidden])") === null;
    }
    const nextMatches = { sections, terms };
    setMatches(q === "" ? null : nextMatches);
    if (q !== "" && sections + terms === 0) setActiveId(null);
    else if (activeId === null) setActiveId(lastActiveId.current);
    // i18n.language: a language switch re-renders every section's text, so an
    // active query is re-applied to the new words.
  }, [query, i18n.language, activeId]);

  const searchStatus = matches === null
    ? ""
    : matches.sections + matches.terms === 0
      ? t("searchNoMatches", { query: query.trim() })
      : t("searchMatches", { query: query.trim(), sections: matches.sections, terms: matches.terms });

  return (
    <Container maxWidth="md" component="section" className="help" disableGutters>
      {inRouter && <RouterHashScroll onHash={followHash} />}
      <div className="help-hero">
        <div className="help-head">
          <p className="help-kicker">{t("eyebrow")}</p>
          <Typography variant="h2">{t("heading")}</Typography>
          <p className="help-lead">{t("lead")}</p>
        </div>

        <div className="help-search">
          <label className="help-search-field" htmlFor={searchId}>
            <Search size={18} aria-hidden />
            <span className="sr-only">{t("searchLabel")}</span>
            {/* The shortcut hint describes the INPUT (the focusable element
                the shortcut applies to), not the decorative, aria-hidden
                `kbd`. `aria-keyshortcuts` names the key for a screen reader,
                which cannot read it off the visible "/" glyph. */}
            <input id={searchId} ref={searchRef} type="search" value={query} placeholder={t("searchPlaceholder")}
              autoComplete="off" onChange={(e) => setQuery(e.target.value)}
              aria-describedby={searchShortcutHintId} aria-keyshortcuts="/" />
            <Tooltip title={t("searchShortcutHint")}>
              <kbd aria-hidden>/</kbd>
            </Tooltip>
          </label>
          {/* Outside the <label>: its text content feeds the input's
              accessible NAME (implicit labelling), and this text is a
              DESCRIPTION, not a second name. */}
          <span id={searchShortcutHintId} className="sr-only">{t("searchShortcutHint")}</span>
          {query !== "" && (
            <button type="button" className="link" onClick={() => setQuery("")}>{t("searchClear")}</button>
          )}
        </div>
        <p className="help-search-status" role="status" aria-live="polite">{searchStatus}</p>
      </div>

      <div className="help-layout">
        <nav ref={tocRef} className="help-toc" aria-label={t("contentsAriaLabel")}>
          <p className="help-kicker">{t("contentsEyebrow")}</p>
          <ul>
            {TOC.map(([id, labelKey]) => (
              <li key={id}>
                <a
                  href={`#${id}`}
                  className={activeId === id ? "active" : undefined}
                  aria-current={activeId === id ? "location" : undefined}
                  onClick={() => activate(id)}
                >
                  {t(labelKey)}
                </a>
              </li>
            ))}
          </ul>
        </nav>

        <div className="help-body" ref={bodyRef}>
          <HelpGuideSections openLink={openLink} />
          <HelpScreenSections openLink={openLink} />
          <HelpAdminSections openLink={openLink} />
          <HelpGlossarySection openLink={openLink} query={query} />
        </div>
      </div>
    </Container>
  );
}
