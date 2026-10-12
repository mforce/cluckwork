import { alpha } from "@mui/material/styles";
import type { Theme, ThemeOptions } from "@mui/material/styles";
import type { ThemeMode, TokenValues } from "./farmTokens";

/** WCAG 2.2 AAA 2.5.5, and the floor `styles/responsive.css` already holds phone controls to. */
const PHONE_TOUCH_TARGET_PX = 44;

/**
 * The one motion curve for the whole app (#864). Borrowed from the
 * high-end-visual-design skill's easing proposal — the only one of that
 * skill's ideas compatible with the confirmed ruled-ledger direction — and
 * confirmed on the issue (2026-09-16). Every component that asks
 * `theme.transitions` for an easing gets this curve rather than MUI's
 * Material default (`cubic-bezier(0.4, 0, 0.2, 1)` and its three siblings).
 */
export const EASE = "cubic-bezier(.32,.72,0,1)";

/**
 * Colour transitions: 160ms. This reuses `theme.transitions.duration.short`
 * rather than a new key: `Button` and `BottomNavigationAction` already read
 * `duration.short` for their own background-color/color/padding transitions
 * (`@mui/material@9.4.0`'s `Button.js` and `BottomNavigationAction.js`), so
 * overriding the one key they already consume gets the direction's number
 * onto every current and future MUI colour transition for free — the same
 * "read what MUI already defaults to" move `elevationScale` makes for shadows.
 */
export const COLOR_TRANSITION_MS = 160;

/**
 * Transform transitions (the press-feedback scale): 240ms. MUI has no
 * built-in consumer of a "transform" duration key, so this is a literal used
 * directly in the `MuiButtonBase` override below instead of a `duration.*`
 * key nothing else would ever read.
 */
const TRANSFORM_TRANSITION_MS = 240;

/**
 * The override half of the farm theme: everything `createFarmTheme`
 * deep-merges over its base theme.
 */
export function farmThemeOverrides({
  tokens, mode, base, cardRadius, panelRadius, pillRadius, controlRadius, selectedNavFill,
}: {
  tokens: TokenValues;
  mode: ThemeMode;
  base: Theme;
  cardRadius: number;
  panelRadius: number;
  pillRadius: number;
  controlRadius: number;
  selectedNavFill: string;
}): ThemeOptions {
  const phone = base.breakpoints.down("md");

  return {
    // Phone-scoped type sizes (title 28/32, rows 16/24). `[phone]` needs
    // `base.breakpoints`, which does not exist until `base` is built, so
    // these live here rather than in the `typography` block above — this
    // second `createTheme` call deep-merges into `base.typography.h2`/
    // `body1` rather than replacing them.
    typography: {
      h2: { [phone]: { fontSize: "1.75rem", lineHeight: 32 / 28 } },
      body1: { [phone]: { fontSize: "1rem", lineHeight: 24 / 16 } },
    },
    components: {
      MuiTypography: {
        defaultProps: {
          // MUI maps `subtitle2` onto `<h6>` by default, which would make a
          // field label a heading and break the role-and-name queries the screen
          // tests are written with. The headings keep their own elements.
          variantMapping: {
            h1: "h1", h2: "h2", h3: "h3", h4: "h4",
            subtitle1: "span", subtitle2: "span",
            body1: "p", body2: "p",
          },
        },
      },
      // Flat by default, so a `Paper` added without thought casts no shadow.
      // Deliberately NOT `variant: "outlined"`: `Paper` only sets
      // `--Paper-shadow` for `variant === "elevation"`, and none of the floats
      // pass a variant, so an outlined default would strip the shadow from every
      // dialog, drawer, menu and snackbar.
      MuiPaper: { defaultProps: { elevation: 0 } },
      MuiCard: {
        defaultProps: { variant: "outlined" },
        styleOverrides: { root: { borderRadius: panelRadius } },
      },
      MuiAccordion: {
        defaultProps: { elevation: 0 },
        styleOverrides: {
          root: {
            border: `1px solid ${tokens["--hairline"]}`,
            borderRadius: controlRadius,
            marginBottom: 12,
            overflow: "hidden",
            "&:before": { display: "none" },
            "&.Mui-expanded": { margin: "0 0 12px" },
            "&:last-of-type": { marginBottom: 0 },
          },
        },
      },
      MuiAccordionSummary: {
        styleOverrides: {
          root: { backgroundColor: tokens["--surface-2"] },
        },
      },
      MuiAccordionDetails: {
        styleOverrides: { root: { padding: 18 } },
      },
      MuiDialog: { styleOverrides: { paper: { borderRadius: cardRadius } } },
      MuiOutlinedInput: {
        styleOverrides: {
          root: {
            borderRadius: controlRadius,
            "&.Mui-focused:not(.Mui-error) .MuiOutlinedInput-notchedOutline": {
              borderColor: tokens["--stat-accent"],
            },
          },
        },
      },
      MuiFormLabel: {
        styleOverrides: {
          root: { "&.Mui-focused:not(.Mui-error)": { color: tokens["--stat-accent"] } },
        },
      },
      MuiCheckbox: {
        styleOverrides: {
          root: {
            "&.Mui-checked:not(.Mui-disabled), &.Mui-indeterminate:not(.Mui-disabled)": {
              color: tokens["--stat-accent"],
            },
          },
        },
      },
      MuiRadio: {
        styleOverrides: {
          root: { "&.Mui-checked:not(.Mui-disabled)": { color: tokens["--stat-accent"] } },
        },
      },
      MuiMenuItem: {
        styleOverrides: {
          root: mode === "dark" ? {
            "&.Mui-selected": {
              backgroundColor: alpha(tokens["--stat-accent"], 0.16),
              "&.Mui-focusVisible": {
                backgroundColor: alpha(tokens["--stat-accent"], 0.28),
              },
            },
            "&.Mui-selected:hover": {
              backgroundColor: alpha(tokens["--stat-accent"], 0.20),
              "@media (hover: none)": {
                backgroundColor: alpha(tokens["--stat-accent"], 0.16),
              },
            },
          } : {},
        },
      },
      // `Autocomplete` sets no elevation on its listbox paper, so it falls to
      // `Paper`'s default of 1 — which this scale flattens. The picker popover is
      // one of #651's floats, so it takes the dialog shadow explicitly.
      MuiAutocomplete: { styleOverrides: { paper: { boxShadow: base.shadows[8] } } },
      // #828 — MUI's own Tooltip default is a hardcoded dark grey
      // (`rgba(97, 97, 97, 0.92)`), the one floating surface in the app that
      // read from no farm token at all. This reads like every other float
      // (Autocomplete's listbox, Dialog's paper): the app's own surface,
      // ink and hairline tokens, the dialog shadow, and the panel radius
      // (a tooltip is an info popover, not a control). `whiteSpace: pre-line`
      // is theme-wide rather than per-callsite `sx`: it preserves a `\n`-
      // joined multi-line `title` (ProvenanceCell's stamp, Audit's JSON)
      // exactly like a native `title` attribute renders one, and a plain
      // single-line tooltip wraps exactly the same under it as under the
      // default `normal`.
      MuiTooltip: {
        styleOverrides: {
          tooltip: {
            backgroundColor: tokens["--surface"],
            color: tokens["--ink"],
            border: `1px solid ${tokens["--hairline"]}`,
            borderRadius: panelRadius,
            boxShadow: base.shadows[8],
            whiteSpace: "pre-line",
          },
          arrow: { color: tokens["--surface"] },
        },
      },
      MuiButton: {
        // `Button` reads `shadows[2]`, `[4]`, `[6]` and `[8]` for the contained
        // variant's rest, hover, focus and press states, so under this scale a
        // primary button would cast the bar shadow on hover and the dialog
        // shadow on press.
        defaultProps: { disableElevation: true },
        styleOverrides: {
          root: {
            // DIRECTION.md line 17 (#864): controls take the 4px control
            // radius, not the pill — the mockup's buttons are 4px rectangles.
            // MuiChip alone keeps the pill.
            borderRadius: controlRadius,
            // Phone-scoped, because `Button variant="text"` is where the app's
            // inline row actions land: an unconditional floor would add ~20px to
            // every row of 22 ledger tables at 1280.
            [phone]: { minHeight: PHONE_TOUCH_TARGET_PX },
          },
        },
        // On text and outlined, `color="primary"` — `Button`'s default, taken
        // by fourteen call sites — is the FOREGROUND, and `palette.primary.main`
        // is raw `--brand`, which #149's dark palette never redeclares: 1.18:1
        // on the dark card. `--stat-accent` equals raw `--brand` in light, and
        // `contained` is left alone because there the brand is the BACKGROUND.
        // `variants`, not v5's `textPrimary`/`outlinedPrimary`: MUI v6 replaced
        // those composites, so the old slot names are dropped in silence.
        variants: [
          {
            props: { variant: "text" as const, color: "primary" as const },
            style: { color: tokens["--stat-accent"] },
          },
          {
            props: { variant: "outlined" as const, color: "primary" as const },
            style: {
              color: tokens["--stat-accent"],
              borderColor: alpha(tokens["--stat-accent"], 0.5),
            },
          },
        ],
      },
      MuiChip: { styleOverrides: { root: { borderRadius: pillRadius } } },
      // No `MuiDialogActions` override: dialog footers stay a right-aligned
      // row at every width (#896, owner decision 2026-09-17, a D3.4 exception
      // like the Daily entry footer), which is the component's own default.
      // A raw `<label>` styles/controls.css still targets (§2.3's `:where(label)`
      // demotion neutralises it only where MUI itself declares the property —
      // `FormControlLabel` never declares `flex-direction`/`gap` on its own
      // root, so the zero-specificity rule was the only source and stacked its
      // checkbox above its label instead of beside it.
      //
      // `flexDirection` and `gap` need different scopes, because MUI's own
      // coverage of the two differs. `FormControlLabel` carries its own
      // `variants` for `labelPlacement="start"|"top"|"bottom"` (`row-reverse`,
      // `column-reverse`, `column`), each with real specificity that already
      // beats `:where(label)`'s zero — so `flexDirection` on `root`
      // unconditionally would sit AHEAD of those variants and win regardless
      // of placement; measured directly, it computed `row` for all four.
      // Scoped to the `labelPlacementEnd` slot instead: MUI composes
      // `styles[labelPlacementEnd]` only when that IS the resolved placement
      // (`end`, the default and the only one this app uses today), leaving
      // the other three to MUI's own variants untouched. `gap` gets no such
      // variant from MUI at ANY placement — it spaces the control from the
      // label with `margin` instead (visible in the root's own
      // `margin-left`/`margin-right`) — so `:where(label)`'s 0.35rem leaks
      // at every placement equally and the reset belongs on `root`.
      MuiFormControlLabel: {
        styleOverrides: {
          root: { gap: 0 },
          labelPlacementEnd: { flexDirection: "row" },
        },
      },
      // #864 — no ripple by default anywhere: motion here is this direction's
      // own choice, not Material's canned ink-spread. `MuiButtonBase` is the
      // base every `Button`, `IconButton`, `Tab`, `Chip` and
      // `BottomNavigationAction` extends, so setting it once here reaches all
      // of them without a per-component default.
      MuiButtonBase: {
        defaultProps: { disableRipple: true },
        styleOverrides: {
          root: {
            // Press feedback (#864 borrow-list item 3): a tap reads as a
            // press. Scoped to the sibling `MuiButton-contained`/`-outlined`
            // classes MUI composes onto this SAME node, never the bare
            // `MuiButtonBase` root or a `MuiButton-text` sibling — the
            // owner's desktop render showed a filled-button tint behind
            // underlined ruled-text actions read as a smudge, so a ruled-text
            // row action (and any other bare `ButtonBase`, e.g. an
            // `IconButton`) gets no transform at all.
            "&.MuiButton-contained, &.MuiButton-outlined": {
              // `transition` is a shorthand for every property in one
              // declaration: naming only `transform` here does not ADD a
              // press transition alongside `Button.js`'s own
              // `background-color`/`box-shadow`/`border-color`/`color` list
              // (`duration.short`, i.e. `COLOR_TRANSITION_MS`) — it REPLACES
              // that whole declaration, because this compound-class selector
              // has higher specificity than `Button.js`'s own single-class
              // rule and both target the same `transition` property. Found by
              // a Codex review of #882 (2026-09-16): a rendered contained
              // button computed `transition: transform 240ms ...` with no
              // colour transition at all, silently dropping the "colour
              // transitions reuse duration.short at 160ms" policy for every
              // contained/outlined button.
              transition: [
                `transform ${TRANSFORM_TRANSITION_MS}ms ${EASE}`,
                `background-color ${COLOR_TRANSITION_MS}ms ${EASE}`,
                `box-shadow ${COLOR_TRANSITION_MS}ms ${EASE}`,
                `border-color ${COLOR_TRANSITION_MS}ms ${EASE}`,
                `color ${COLOR_TRANSITION_MS}ms ${EASE}`,
              ].join(", "),
              "&:active": { transform: "scale(0.98)" },
              // `theme.motion.reducedMotion: "system"` above covers every
              // transition MUI itself builds through `getTransitionStyles()`;
              // this one is a plain style object, not built through that
              // helper, so it repeats the same media query by hand.
              "@media (prefers-reduced-motion: reduce)": { transition: "none" },
            },
          },
        },
      },
      // Tab bar variant B ("ruled"), owner pick (2026-09-16): no bar shadow,
      // a hairline top rule instead. #829 mounts the component; this is its
      // theme, so the conversion inherits the pick rather than deciding it.
      MuiBottomNavigation: {
        styleOverrides: {
          root: {
            boxShadow: "none",
            borderTop: `1px solid ${tokens["--hairline"]}`,
            backgroundColor: tokens["--surface"],
          },
        },
      },
      MuiBottomNavigationAction: {
        styleOverrides: {
          root: {
            minHeight: PHONE_TOUCH_TARGET_PX,
            // The `icon` prop renders as a direct child with no wrapper class
            // (`bottomNavigationActionClasses` names only `root`, `iconOnly`,
            // `selected`, `label` — no `icon` slot), so sizing it has to reach
            // through to whatever SVG the caller passes, the same way
            // `MuiButton`'s pill radius above reaches every button regardless
            // of what rendered it.
            "& > svg": { width: 24, height: 24 },
            // The 2px `--stat-accent` rule above the active tab (variant B) —
            // the same device the sidebar's active-item rule uses (`.sidebar
            // nav a.active`, `styles.css`), so the two shells say "active"
            // the same way.
            "&.Mui-selected": {
              color: tokens["--stat-accent"],
              boxShadow: `inset 0 2px 0 0 ${tokens["--stat-accent"]}`,
            },
          },
          label: {
            // One label size at every state. MUI's own default bumps a
            // selected label from 12px to 14px
            // (`BottomNavigationAction.js`'s `&.selected` rule), which this
            // direction does not want: labels are 500-weight 11px whether
            // selected or not, and only the colour (MUI's own
            // `palette.primary.main` on `.Mui-selected`) and the rule above
            // the icon carry "active".
            fontSize: 11,
            fontWeight: 500,
            "&.Mui-selected": { fontSize: 11 },
          },
        },
      },
      // `Tabs`' own default (`textColor="primary"`) paints the selected label
      // with `palette.primary.main` (raw `--brand`), which styles/tokens.css keeps
      // out of the dark block on purpose and so reads near-invisible on a
      // dark surface. `--stat-accent` is the same "active" device
      // `MuiBottomNavigationAction` and the sidebar already use.
      MuiTabs: {
        styleOverrides: {
          indicator: { backgroundColor: tokens["--stat-accent"] },
        },
      },
      MuiTab: {
        styleOverrides: {
          root: {
            "&.Mui-selected": { color: tokens["--stat-accent"] },
          },
        },
      },
      // Ledger row heights (DIRECTION.md): 36px desktop, 52px phone.
      MuiTableRow: {
        styleOverrides: {
          root: { height: 36, [phone]: { height: 52 } },
        },
      },
      // #832 — real-device #441 repro, reproduced again on this PR's first
      // Playwright pass: `TableContainer`'s own default (`width: 100%;
      // overflow-x: auto`, `TableContainer.js`) correctly scrolls the table
      // WITHIN itself, but mobile browsers' initial LAYOUT viewport sizing
      // still measures the un-clipped table's raw content width and inflates
      // `window.innerWidth` past the visual viewport — `overflow-x` on every
      // ancestor does not stop it, confirmed on `/flocks` at 390 (measured
      // `document.documentElement.scrollWidth` 941px against a 390px frame,
      // `phone.spec.ts`'s "no walked screen overflows" walk). `contain:
      // layout` is what closes the gap: it tells the browser this element's
      // internal layout can never affect an ancestor's size. `styles/responsive.css`'s
      // own `table.data` phone rule (§2.2) already carries this for every
      // unconverted ledger; this is the same fix for MUI's `TableContainer`,
      // the component D3.2 names as this app's phone table treatment going
      // forward.
      MuiTableContainer: {
        styleOverrides: {
          root: {
            [phone]: {
              contain: "layout",
              // Local covers hide the scroll shadows when the corresponding edge is reached.
              background:
                "linear-gradient(to right, var(--surface) 40%, transparent) 0 0 / 2.25rem 100% no-repeat local,"
                + "linear-gradient(to left, var(--surface) 40%, transparent) 100% 0 / 2.25rem 100% no-repeat local,"
                + "linear-gradient(to right, var(--scroll-cue), transparent) 0 0 / 0.85rem 100% no-repeat scroll,"
                + "linear-gradient(to left, var(--scroll-cue), transparent) 100% 0 / 0.85rem 100% no-repeat scroll",
            },
          },
        },
      },
      // #832 — closes the gap the comment above used to carry: this is the
      // first slice to mount a real MUI `Table` (Customers/Products/Grades/
      // Flocks/Users). `TableCell` spreads `theme.typography.body2` as its
      // base (`TableCell.js`) — 0.95rem at MUI's default 1.43 line-height
      // (~21.7px), never this theme's row scale — so left alone it would sit
      // taller than DIRECTION.md's 14/20 desktop / 16/24 phone rows and carry
      // no tabular numerals. Pinning `fontSize`/`lineHeight` here to the same
      // numbers `body1` already carries (rather than pointing `variant` at
      // `body1`, which would also move the `variantMapping` element) reaches
      // every `TableCell` regardless of context.
      //
      // Padding is `table.data td`'s own value rather than `size="small"`'s
      // symmetric default: the default's extra 16px per cell pushed Flocks'
      // widest row past its container at 1280. The zero left inset assumed a
      // borderless ledger; every table now sits in a bordered panel, so the
      // first cell needs the last cell's own 1rem, or its text sits flush
      // against the border. `:where()` holds this at the base rule's own
      // specificity, so FieldConsole's and Dashboard's own higher-specificity
      // overrides still win where they already gave a table a correct inset.
      MuiTableCell: {
        styleOverrides: {
          root: {
            fontSize: "0.875rem",
            lineHeight: 20 / 14,
            fontVariantNumeric: "tabular-nums",
            padding: "0.6rem 1rem 0.6rem 0",
            "&:where(:first-of-type)": { paddingLeft: "1rem" },
            [phone]: { fontSize: "1rem", lineHeight: 24 / 16 },
          },
        },
      },
      // Sidebar shell (D2 pair 12, #829). `Drawer`'s paper defaults to
      // `background.paper`; DIRECTION.md's confirmed nav rail is `--lavender`
      // tinted paper with `--stat-accent` text, not the aubergine `--brand`
      // slab `.sidebar` painted before this slice — brand appears in exactly
      // four places now (farm name, active nav item, primary button, focus
      // ring), and this is the rail's share of that.
      MuiDrawer: {
        styleOverrides: {
          paper: {
            backgroundColor: tokens["--lavender"],
            borderRight: `1px solid ${tokens["--hairline"]}`,
          },
        },
      },
      // The group heading keeps its own CSS class (`.nav-group-label` /
      // `.more-group-label`, styles/shell.css — D4/#824's guard keys on those two
      // selectors for the caps casing), so this only neutralises MUI's own
      // subheader chrome (its sticky positioning and background) rather than
      // fighting the class for size and colour.
      MuiListSubheader: {
        styleOverrides: {
          root: {
            position: "static",
            backgroundColor: "transparent",
            lineHeight: "inherit",
          },
        },
      },
      MuiListItemButton: {
        styleOverrides: {
          root: {
            borderLeft: "3px solid transparent",
            gap: 8,
            "&.Mui-selected, &.Mui-selected:hover": {
              backgroundColor: selectedNavFill,
              borderLeftColor: selectedNavFill,
              color: tokens["--on-brand"],
              fontWeight: 600,
              "& .MuiListItemIcon-root": { color: "inherit" },
            },
            "&:hover": { backgroundColor: tokens["--surface-2"] },
          },
        },
      },
      // #834 — DIRECTION.md's link language for a real MUI `Link`: ink text,
      // underlined in the 28% ink rule at rest, full ink on hover and focus.
      // No screen renders `@mui/material`'s `Link` today (they render
      // `Typography component={Link}` from react-router, styled by
      // `:where(.content a)` in styles/shell.css instead), so this is groundwork —
      // it has to exist before a screen can adopt the real component, and
      // `farmTheme.policy.test.ts` is where it is pinned.
      MuiLink: {
        defaultProps: { underline: "always" },
        styleOverrides: {
          root: {
            color: tokens["--ink"],
            textDecorationColor: tokens["--link-rule"],
            textUnderlineOffset: "2px",
            "&:hover, &:focus-visible": {
              textDecorationColor: tokens["--ink"],
            },
          },
        },
      },
    },
  };
}
