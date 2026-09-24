import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { KeyboardEvent, MouseEvent, ReactNode } from "react";
import { Box, TableContainer, Typography } from "@mui/material";
import { useTranslation } from "react-i18next";

export const CONSOLE_PANEL_SX = {
  border: "1px solid var(--rule)",
  borderRadius: "var(--r-panel)",
  p: 2,
  bgcolor: "var(--surface)",
  minWidth: 0,
};
export const CONSOLE_LINK_SX = {
  minWidth: 0, px: 0, color: "var(--link)", fontWeight: 700,
  textDecoration: "underline", textDecorationColor: "var(--link-rule)", textUnderlineOffset: "3px",
  "&:hover": { textDecoration: "underline" },
  // #930 — MUI's own dark-mode action.disabled (rgba(255,255,255,0.3)) clears
  // only ~2.6:1 against --surface/--surface-2, under the 3:1 floor. --muted
  // reads as inert without borrowing the idle link's own colour. `opacity: 1`
  // overrides the global `:where(button:disabled) { opacity: .5 }`
  // (styles.css) the same way the existing busy-button exception does
  // (`:where(button:disabled[aria-busy="true"])`) — left unset, --muted at
  // half opacity measures 2.84:1/2.68:1, still under 3:1.
  "&.Mui-disabled": { color: "var(--muted)", opacity: 1 },
};
// #908 — a destructive action (deactivate, archive, deplete, disable) must
// read as distinct from CONSOLE_LINK_SX's edit link by more than colour: pair
// this with a leading icon (e.g. TriangleAlert) at every call site, so shape
// carries the signal colour alone would miss for a colour-blind reader.
// `--error`, not `--danger`: `--danger` was designed as a filled-button
// background, and dark mode's value measures ~3:1 as small text, under AA
// (codex review round 3); `--error` is the token already tuned for that case.
export const CONSOLE_DESTRUCTIVE_LINK_SX = {
  ...CONSOLE_LINK_SX,
  color: "var(--error)",
  textDecorationColor: "var(--error)",
  "&:hover": { textDecoration: "underline", backgroundColor: "var(--tint-danger)" },
};
// #831 Concept B: cancel the panel padding so the header divider reaches both edges.
export const CONSOLE_PAPER_HEAD_SX = {
  display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: "15px",
  mx: -2, mt: -2, mb: 2, px: 2, py: "14px", borderBottom: "1px solid var(--rule)",
  "&& h3": { m: 0 },
};

export function ConsoleSubhead({ title, caption }: { title: string; caption: string }) {
  return <Box sx={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 2, mt: "22px", mb: 1, "&& h3": { m: 0 } }}>
    <Typography component="h3" variant="h3">{title}</Typography>
    <Typography component="span" sx={{ color: "text.secondary", fontSize: ".75rem", textAlign: "right" }}>{caption}</Typography>
  </Box>;
}

export const CONSOLE_SPLIT_SX = {
  display: "grid",
  gridTemplateColumns: { xs: "minmax(0, 1fr)", md: "minmax(0, 1.6fr) minmax(240px, .8fr)" },
  gap: 2,
  my: 2,
};
export const CONSOLE_FORM_SX = {
  display: "grid",
  gridTemplateColumns: { xs: "minmax(0, 1fr)", md: "repeat(2, minmax(0, 1fr))" },
  gap: 2,
  alignItems: "end",
  alignContent: "start",
  "& > *": { minWidth: 0, width: "100%", maxWidth: "100%" },
};
export const CONSOLE_TICKET_SX = {
  ...CONSOLE_PANEL_SX,
  display: "grid",
  gridTemplateColumns: { xs: "minmax(0, 1fr)", md: "minmax(0, 1fr) minmax(230px, .55fr)" },
  p: 0,
  my: 2,
};
export const CONSOLE_TICKET_FORM_SX = {
  ...CONSOLE_FORM_SX,
  gridTemplateColumns: { xs: "minmax(0, 1fr)", md: "repeat(4, minmax(0, 1fr))" },
  gap: 1.5,
};
export const CONSOLE_TICKET_CHECK_SX = {
  p: 2,
  minWidth: 0,
  bgcolor: "var(--surface-2)",
  borderTop: { xs: "1px solid var(--rule)", md: 0 },
  borderLeft: { md: "1px solid var(--rule)" },
};

export function ConsoleSummary({ label, items }: {
  label: string;
  items: { label: string; value: ReactNode }[];
}) {
  return (
    <Box component="dl" aria-label={label} sx={{
      display: { xs: "grid", md: "flex" }, gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
      flexWrap: "wrap", gap: "8px 24px", my: 2, py: 1.25,
      borderTop: "1px solid var(--rule)", borderBottom: "1px solid var(--rule)",
    }}>
      {items.map((item) => <Box key={item.label} sx={{ minWidth: 0, display: "flex", gap: .75, flexWrap: "wrap", alignItems: "baseline" }}>
        <Typography component="dt" variant="body2" sx={{ fontSize: ".7rem", color: "text.secondary" }}>{item.label}</Typography>
        <Typography component="dd" variant="body2" sx={{ m: 0, fontSize: ".8rem", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{item.value}</Typography>
      </Box>)}
    </Box>
  );
}

// Concept B keeps the settlement and stock rails dark in both colour modes (#831).
export const CONSOLE_RAIL_SX = {
  ...CONSOLE_PANEL_SX,
  bgcolor: "#2c2429",
  color: "#fff",
  "& .MuiTypography-root": { color: "inherit" },
};

export function FieldConsole({ children }: { children: ReactNode }) {
  return (
    <Box component="section" data-field-console sx={{
      minWidth: 0,
      "&& h2": { typography: "h2", mb: 1 },
      "&& h3": { typography: "h3", mt: 2.5, mb: 1 },
      "& > p": { fontSize: ".8125rem", lineHeight: 1.45 },
      "& aside p.muted": { fontSize: ".75rem", lineHeight: 1.45 },
      "& aside h3": { mt: 0 },
      "& .MuiTableCell-root": { fontSize: "0.75rem", py: 1.25, px: 1 },
      // Footer variants set their own muted colour and weight in MUI.
      "& .MuiTableCell-footer": { color: "var(--ink)", fontWeight: 600 },
      "& .MuiTableContainer-root .MuiTableCell-alignRight": { whiteSpace: "nowrap" },
      "& .MuiTableCell-head": {
        fontSize: "0.625rem", letterSpacing: ".06em", textTransform: "uppercase",
      },
    }}>
      {children}
    </Box>
  );
}

// Keep the swipe cue outside the scrolling element so it stays visible as columns move.
export function LedgerTableContainer({ children, alwaysShowSwipeCue = false, scrollHint = "columns" }: {
  children: ReactNode;
  alwaysShowSwipeCue?: boolean;
  // #908 — the setup lists' table also scrolls vertically within a bounded
  // region (the bottom inspector docks below it), so that cue names both axes.
  scrollHint?: "columns" | "columnsAndRows";
}) {
  const { t } = useTranslation("common");
  return (
    <Box sx={{
      minWidth: 0, borderTop: "2px solid var(--ink)", borderBottom: "1px solid var(--rule)",
      // #908 — a nested flex column so `TableContainer` below is the ONE
      // element that both scrolls and is `position: sticky`'s containing
      // block. Outside a flex parent (this component's seven other,
      // unbounded callers) these properties are simply inert.
      display: "flex", flexDirection: "column", flex: "1 1 auto", minHeight: 0,
    }}>
      <Typography component="p" variant="body2" sx={{
        display: alwaysShowSwipeCue ? "block" : { xs: "block", md: "none" },
        flex: "0 0 auto",
        m: 0, py: .5, px: 1,
        fontSize: ".65rem",
        textAlign: "right",
        bgcolor: "var(--surface-2)",
        color: "text.secondary",
      }}>
        {t(scrollHint === "columnsAndRows" ? "swipeColumnsScrollRows" : "swipeColumns")}
      </Typography>
      <TableContainer sx={{ flex: "1 1 auto", minHeight: 0, overflow: "auto" }}>{children}</TableContainer>
    </Box>
  );
}

// #908 — a sticky header keeps column labels visible while `LedgerTableContainer`
// scrolls a bounded-height table region vertically (Concept B's inspector docks
// below that same region, so the table's own scroll is what stays contained).
export const STICKY_TABLE_HEAD_SX = { position: "sticky" as const, top: 0, zIndex: 1, bgcolor: "var(--surface)" };

export interface InspectorField {
  label: string;
  value: ReactNode;
}

// #908 — the setup lists' selected-record panel, docked below the table
// (Concept B, issue #908's owner-approved direction). Renders nothing but the
// empty prompt until a row is selected.
export function RecordInspector({ ariaLabel, title, fields, actions, emptyMessage }: {
  ariaLabel: string;
  title?: ReactNode;
  fields?: InspectorField[];
  actions?: { primary: ReactNode; secondary?: ReactNode; destructive?: ReactNode };
  emptyMessage: string;
}) {
  if (title === undefined) {
    return (
      <Box component="aside" role="region" aria-label={ariaLabel} sx={{ p: 2, color: "text.secondary", fontSize: ".8125rem" }}>
        {emptyMessage}
      </Box>
    );
  }
  return (
    <Box component="aside" role="region" aria-label={ariaLabel} sx={{ minWidth: 0 }}>
      <Box sx={{ ...CONSOLE_RAIL_SX, borderRadius: 0, border: 0, p: "4px 18px" }}>
        <Typography component="h3" variant="h3" tabIndex={-1} sx={{
          m: 0, color: "inherit",
          "&:focus-visible": { outline: "2px solid currentColor", outlineOffset: 2 },
        }}>{title}</Typography>
      </Box>
      {fields && fields.length > 0 && (
        <Box component="dl" sx={{
          m: 0, p: "2px 18px 4px", display: "grid",
          gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))" },
          columnGap: "16px",
        }}>
          {fields.map((field, i) => (
            <Box key={i} sx={{
              display: "grid", gridTemplateColumns: "86px 1fr", gap: "7px",
              borderBottom: "1px solid var(--rule)", fontSize: ".75rem",
            }}>
              <Typography component="dt" sx={{ color: "text.secondary", fontSize: "inherit" }}>{field.label}</Typography>
              <Typography component="dd" sx={{ m: 0, fontWeight: 650, fontSize: "inherit", overflowWrap: "anywhere" }}>
                {field.value}
              </Typography>
            </Box>
          ))}
        </Box>
      )}
      {actions && <Box sx={{ display: "flex", flexWrap: "wrap", alignItems: "center", columnGap: .75, px: 1.5, pb: 1 }}>
        {actions.primary}
        {actions.secondary}
        {actions.destructive && <Box sx={{ display: "flex", flexWrap: "wrap", alignItems: "center", columnGap: .75, borderLeft: "1px solid var(--rule)", pl: .75 }}>
          {actions.destructive}
        </Box>}
      </Box>}
    </Box>
  );
}

// #908 — a flex column so the table owns its own scroll above a bottom-docked
// inspector, in the same region, never overlapping it (Concept B).
export function ListInspectorPane({ table, inspector, tableLabel }: { table: ReactNode; inspector: ReactNode; tableLabel: string }) {
  const paneRef = useRef<HTMLDivElement>(null);
  const inspectorRef = useRef<HTMLDivElement>(null);
  const tableRef = useRef<HTMLDivElement>(null);
  const [keyboardSelection, setKeyboardSelection] = useState(0);
  const [paneTop, setPaneTop] = useState(0);
  useLayoutEffect(() => {
    const tableRegion = tableRef.current;
    const inspectorRegion = inspectorRef.current;
    if (!tableRegion || !inspectorRegion) return;
    let origin: { element: HTMLElement; rowIndex: number } | null = null;
    const rememberFocus = (event: FocusEvent) => {
      const target = event.target;
      if (!(target instanceof HTMLElement)) return;
      if (inspectorRegion.contains(target)) {
        const rows = [...tableRegion.querySelectorAll('tr[aria-selected]')];
        origin = { element: target, rowIndex: rows.findIndex((row) => row.getAttribute("aria-selected") === "true") };
      } else if (!target.closest('[role="dialog"]')) {
        origin = null;
      }
    };
    // A closing dialog can outlive the row refresh. Recover once its focus is released.
    const observer = new MutationObserver(() => {
      if (!origin || origin.element.isConnected) return;
      if (inspectorRegion.querySelector("h3")) {
        origin = null;
        return;
      }
      if (document.activeElement !== document.body) return;
      const rows = tableRegion.querySelectorAll<HTMLElement>('tr[aria-selected]');
      const target = rows[Math.min(origin.rowIndex, rows.length - 1)] ?? tableRegion;
      origin = null;
      target.focus();
    });
    document.addEventListener("focusin", rememberFocus);
    observer.observe(document.body, { childList: true, subtree: true });
    return () => {
      document.removeEventListener("focusin", rememberFocus);
      observer.disconnect();
    };
  }, []);
  useLayoutEffect(() => {
    const pane = paneRef.current;
    if (!pane) return;
    const measure = () => setPaneTop(pane.getBoundingClientRect().top + window.scrollY);
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(document.body);
    window.addEventListener("resize", measure);
    return () => {
      observer.disconnect();
      window.removeEventListener("resize", measure);
    };
  }, []);
  useLayoutEffect(() => {
    if (keyboardSelection > 0) inspectorRef.current?.querySelector<HTMLElement>("h3")?.focus();
  }, [keyboardSelection]);
  return (
    <Box ref={paneRef} onKeyDown={(event) => {
      if (event.target instanceof HTMLTableRowElement && event.target.tabIndex === 0
          && (event.key === "Enter" || event.key === " ")) {
        setKeyboardSelection((selection) => selection + 1);
      }
    }} sx={{
      display: "flex", flexDirection: "column", minWidth: 0,
      border: "1px solid var(--rule)", borderRadius: "var(--r-panel)", overflow: "hidden",
      height: {
        xs: `max(280px, calc(100dvh - ${paneTop}px - var(--tabbar-h) - 8px))`,
        md: "clamp(280px, calc(100dvh - 380px), 520px)",
      },
    }}>
      <Box ref={tableRef} role="region" aria-label={tableLabel} tabIndex={-1} sx={{
        flex: "1 1 auto", minHeight: 0, display: "flex", flexDirection: "column",
        "&:focus-visible": { outline: "2px solid var(--ink)", outlineOffset: -2 },
      }}>{table}</Box>
      <Box ref={inspectorRef} onKeyDown={(event) => {
        if (event.key !== "Escape" || !(event.target instanceof Node)
            || !event.currentTarget.contains(event.target)) return;
        event.preventDefault();
        event.stopPropagation();
        paneRef.current?.querySelector<HTMLElement>('tr[aria-selected="true"]')?.focus();
      }} sx={{
        flex: "0 0 auto", maxHeight: { xs: 260, md: 220 }, overflow: "auto",
        borderTop: "1px solid var(--rule)", bgcolor: "var(--surface)",
      }}>
        {inspector}
      </Box>
    </Box>
  );
}

// #908 — shared row-selection wiring: click OR Enter/Space selects the row,
// `aria-selected` exposes it to assistive tech (issue #908's keyboard/AX
// requirement), and the accent bar makes selection visible without relying on
// colour alone.
export function selectableRowProps(selected: boolean, onSelect: () => void) {
  return {
    // A row action (a button or link inside the row) owns its own click; the
    // row's own selection is a fallback for the rest of the row's surface.
    onClick: (e: MouseEvent<HTMLTableRowElement>) => {
      if ((e.target as HTMLElement).closest("button, a")) return;
      onSelect();
    },
    onKeyDown: (e: KeyboardEvent<HTMLTableRowElement>) => {
      if (e.key !== "Enter" && e.key !== " ") return;
      if ((e.target as HTMLElement).closest("button, a")) return;
      e.preventDefault();
      onSelect();
    },
    tabIndex: 0,
    "aria-selected": selected,
    sx: {
      cursor: "pointer",
      ...(selected && { bgcolor: "var(--tint-accent)", boxShadow: "inset 3px 0 var(--brand)" }),
    },
  };
}

// #908 — a filter toggle, an archive/reactivate write, or any other refresh
// can drop the selected row out of the visible set; without this, the stale
// id can resurface as "selected" the moment that row becomes visible again,
// with no new selection from the user.
export function useClampSelection(
  visibleIds: readonly string[], selectedId: string | null, setSelectedId: (id: string | null) => void,
) {
  useEffect(() => {
    if (selectedId !== null && !visibleIds.includes(selectedId)) setSelectedId(null);
  }, [visibleIds, selectedId, setSelectedId]);
}
