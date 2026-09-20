// web/src/components/FlockPickerDialog.tsx
import { useEffect, useId, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Box, Button, Dialog, IconButton, TextField, Typography } from "@mui/material";
import { X } from "lucide-react";
import { listFlocks } from "../api/cluckwork";
import type { Flock } from "../api/cluckwork";

// #918 — Codex review, finding 1 (round 3). The picker's own discovery,
// server-paged exactly like the shared NamedEntityPicker engine (50-row
// pages, 250ms debounce): filtering an already-loaded, capped flock array
// made a flock past that cap unreachable by search on a large farm. This
// search is independent of the page-scoped `flocks` list the rest of the
// Dashboard reads.
const PAGE_SIZE = 50;
const DEBOUNCE_MS = 250;

// #916 — the Lay rate card's own scope, independent of every other panel.
export type FlockScope = { kind: "all" } | { kind: "flock"; flock: Flock };

// #918 round 4 — extracted out of Dashboard.tsx: the dialog owns every piece
// of state a flock lookup needs (search text, the paged result set, its own
// loading/error state, keyboard roving focus) so Dashboard.tsx only holds
// whether the dialog is open and the scope a pick produced. Everything below
// is self-contained and independently testable; Dashboard wires it to the
// page's own scope state through the three callback props.
export function FlockPickerDialog({
  open, onClose, scope, accessibleCount, onPickAll, onPickFlock,
}: {
  open: boolean;
  onClose: () => void;
  scope: FlockScope;
  accessibleCount: number;
  onPickAll: () => void;
  onPickFlock: (flock: Flock) => void;
}) {
  const { t } = useTranslation("dashboard");
  // #918 — Codex review, finding 1: Retry/Load more/loading strings reuse the
  // shared picker catalog rather than minting new ones — the page namespace
  // still owns anything page-specific (the dialog's own title, labels).
  const { t: tp } = useTranslation("namedEntityPicker");
  const pickerTitleId = useId();
  const searchInputId = useId();

  const [searchQuery, setSearchQuery] = useState("");
  const [results, setResults] = useState<Flock[]>([]);
  const [cursor, setCursor] = useState(0);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(false);
  // #918 — Codex review, round 4, finding 3: a failed discovery request used
  // to render as "No matching flocks" — indistinguishable from a real empty
  // result. This distinguishes the two, with its own Retry.
  const [discoveryFailed, setDiscoveryFailed] = useState(false);
  const [retryTick, setRetryTick] = useState(0);

  // The search box always starts empty on a fresh open, never showing the
  // PREVIOUS session's leftover text.
  useEffect(() => { if (open) setSearchQuery(""); }, [open]);

  // #918 — Codex review, round 4, finding 1. `queryGenRef` bumps on every
  // effect run — a new search, the dialog opening or closing, or a Retry —
  // so `loadMore` below and this effect's own fetch can tell whether the
  // query they were dispatched for is still the current one. Results and the
  // cursor are cleared in the SAME tick a query changes, not only once the
  // fresh page lands: without that, a `loadMore` click during the debounce
  // window would still read the PREVIOUS query's cursor and could append the
  // new query's page onto the old query's results.
  const queryGenRef = useRef(0);
  useEffect(() => {
    const gen = ++queryGenRef.current;
    if (!open) return;
    setLoading(true);
    setDiscoveryFailed(false);
    setResults([]);
    setCursor(0);
    setHasMore(false);
    const timer = window.setTimeout(() => {
      listFlocks({ search: searchQuery.trim() || undefined, eligibility: "active-and-depleted", limit: PAGE_SIZE, offset: 0 })
        .then((page) => {
          if (gen !== queryGenRef.current) return; // superseded by a newer query, or the dialog closed
          setResults(page);
          setCursor(page.length);
          setHasMore(page.length === PAGE_SIZE);
          setLoading(false);
        })
        .catch(() => {
          if (gen !== queryGenRef.current) return;
          setDiscoveryFailed(true);
          setLoading(false);
        });
    }, DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [open, searchQuery, retryTick]);

  // One click = one extension request, at the PAINTED cursor, tied to the
  // generation active when it was dispatched — matches the shared picker
  // engine's own "Load more" contract (picker-ui.md).
  const loadMore = () => {
    const gen = queryGenRef.current;
    setLoading(true);
    listFlocks({ search: searchQuery.trim() || undefined, eligibility: "active-and-depleted", limit: PAGE_SIZE, offset: cursor })
      .then((page) => {
        if (gen !== queryGenRef.current) return; // the query changed, or the dialog closed, since this was dispatched
        setResults((prev) => [...prev, ...page]);
        setCursor((c) => c + page.length);
        setHasMore(page.length === PAGE_SIZE);
        setLoading(false);
      })
      .catch(() => {
        if (gen !== queryGenRef.current) return;
        setDiscoveryFailed(true);
        setLoading(false);
      });
  };

  // #918 — Codex review, finding 3 (round 3): arrow/Home/End move focus among
  // the dialog's own choice buttons ("All flocks" reachable first, matching
  // the mockup), mirroring the shared picker engine's own keyboard contract
  // (FR-032/picker-ui.md) without adopting its Autocomplete/combobox markup —
  // these stay plain, individually-focusable buttons, so this is a roving
  // FOCUS move, not a roving tabindex/aria-activedescendant pattern.
  //
  // Round 4, finding 5: a DISABLED "Load more" (mid-fetch) is excluded, or
  // End/ArrowDown could land focus on a button that cannot be activated.
  const bodyRef = useRef<HTMLDivElement>(null);
  const onBodyKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(e.key)) return;
    const container = bodyRef.current;
    if (!container) return;
    const buttons = Array.from(container.querySelectorAll<HTMLButtonElement>("button")).filter((b) => !b.disabled);
    if (buttons.length === 0) return;
    let i = buttons.indexOf(document.activeElement as HTMLButtonElement);
    if (i === -1) i = 0;
    else if (e.key === "Home") i = 0;
    else if (e.key === "End") i = buttons.length - 1;
    else if (e.key === "ArrowDown") i = Math.min(buttons.length - 1, i + 1);
    else if (e.key === "ArrowUp") i = Math.max(0, i - 1);
    e.preventDefault();
    buttons[i]?.focus();
  };
  // ArrowDown from the search field itself lands on the first choice ("All
  // flocks" — reachable first, per the finding), matching the mockup's own
  // search-to-results handoff.
  const onSearchKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key !== "ArrowDown") return;
    const first = bodyRef.current?.querySelector<HTMLButtonElement>("button:not(:disabled)");
    if (!first) return;
    e.preventDefault();
    first.focus();
  };

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="xs" aria-labelledby={pickerTitleId}>
      <Box sx={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 1.25, p: 2, pb: 1 }}>
        <Typography id={pickerTitleId} sx={{ fontWeight: 700 }}>{t("chooseFlockTitle")}</Typography>
        <IconButton onClick={onClose} aria-label={t("closeFlockSelectorAction")} sx={{ width: 44, height: 44 }}>
          <X size={22} />
        </IconButton>
      </Box>
      <Box sx={{ px: 2 }}>
        <Typography component="label" htmlFor={searchInputId} variant="caption" sx={{ display: "block", mb: 0.5 }}>
          {t("searchAccessibleFlocksLabel")}
        </Typography>
        <TextField
          id={searchInputId} type="search" fullWidth size="small" autoComplete="off"
          value={searchQuery} onChange={(e) => setSearchQuery(e.target.value)}
          onKeyDown={onSearchKeyDown}
          placeholder={t("searchByNamePlaceholder")}
        />
      </Box>
      {/* `bodyRef`/`onBodyKeyDown`: arrow/Home/End move focus among every
          ENABLED button below, "All flocks" reachable first since it is the
          first button in DOM order — pinned ABOVE the scrolling result list,
          never a row inside it (SELECTION.md / the mockup's #allChoice). */}
      <div ref={bodyRef} onKeyDown={onBodyKeyDown}>
        <Button
          fullWidth onClick={onPickAll}
          aria-pressed={scope.kind === "all"}
          sx={{ justifyContent: "space-between", textTransform: "none", mt: 1.5, mx: 2, width: "calc(100% - 32px)", "&&": { minHeight: 44 } }}
        >
          <span>{t("allFlocksOption")}</span>
          <Typography component="span" variant="caption" color="text.secondary">
            {t("accessibleFlocksCount", { count: accessibleCount })}
          </Typography>
        </Button>
        {discoveryFailed ? (
          <Box sx={{ px: 2, mt: 1.5 }}>
            <Alert severity="error" className="error" action={
              <Button color="inherit" size="small" onClick={() => setRetryTick((n) => n + 1)}>{tp("retry")}</Button>
            }>
              {t("flockDiscoveryUnavailableMessage")}
            </Alert>
          </Box>
        ) : (
          <>
            <Typography variant="caption" color="text.secondary" role="status" sx={{ display: "block", px: 2, mt: 1.5, mb: 0.5 }}>
              {loading ? tp("loading") : t("matchingFlocksCount", { count: results.length })}
            </Typography>
            <Box component="ul" role="list" aria-label={t("flockScopeResultsLabel")}
              sx={{ listStyle: "none", m: 0, p: 0, maxHeight: 180, overflow: "auto", borderTop: "1px solid var(--rule)" }}
            >
              {results.length === 0 && !loading ? (
                <Typography sx={{ p: 1.5, fontSize: "13px" }}>{t("noMatchingFlocksMessage")}</Typography>
              ) : results.map((f) => (
                <Box component="li" key={f.id}>
                  <Button
                    fullWidth onClick={() => onPickFlock(f)}
                    aria-pressed={scope.kind === "flock" && scope.flock.id === f.id}
                    sx={{
                      justifyContent: "flex-start", textAlign: "left", textTransform: "none",
                      borderRadius: 0, borderBottom: "1px solid var(--rule)", "&&": { minHeight: 44 },
                    }}
                  >
                    {f.name}
                  </Button>
                </Box>
              ))}
            </Box>
            {hasMore && (
              <Button
                fullWidth onClick={loadMore} disabled={loading}
                sx={{ textTransform: "none", "&&": { minHeight: 44 } }}
              >
                {tp("loadMore")}
              </Button>
            )}
          </>
        )}
      </div>
    </Dialog>
  );
}
