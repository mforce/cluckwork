import {
  Bird, Boxes, ChartColumn, CircleHelp, ClipboardList, Download, Egg, History,
  LayoutDashboard, Package, ScrollText, ShoppingCart, Tags, UserCog, Users,
  Wallet, Droplets,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import type { Role } from "../auth/claims";

// One nav model, three renderers (sidebar, bottom tabs, the More sheet). The
// role gates live HERE only — a second copy in the mobile nav would be the
// exact drift that lets a ReadOnly user see a Sales tab the API then rejects.
export interface NavEntry {
  to: string;
  label: string;
  Icon: LucideIcon;
  end?: boolean;
}

export interface NavGroup {
  label: string;
  entries: NavEntry[];
}

// The destinations, grouped by job, filtered to what this role can actually
// reach. Same conditions the sidebar has always used (#103): the API enforces
// the policy regardless, this is only what to bother showing.
export function navGroups(role: Role, isAdmin: boolean): NavGroup[] {
  // Denied is a token whose role claims none of the app understood (claims.ts):
  // the API refuses it everywhere, so it gets only the reads every principal
  // has, never a production or sales destination that would 403. Folding it in
  // here fixes the sidebar and the tab bar at once — the point of one model.
  const canProduce = role !== "Sales" && role !== "ReadOnly" && role !== "Denied";
  const notReadOnly = role !== "ReadOnly" && role !== "Denied";

  const groups: NavGroup[] = [
    { label: "Overview", entries: [{ to: "/", label: "Dashboard", Icon: LayoutDashboard, end: true }] },
  ];

  if (canProduce) {
    groups.push({
      label: "Production",
      entries: [
        { to: "/daily-entry", label: "Daily entry", Icon: ClipboardList },
        { to: "/flocks", label: "Flocks", Icon: Bird },
        { to: "/water", label: "Water", Icon: Droplets },
        { to: "/inventory", label: "Inventory", Icon: Boxes },
      ],
    });
  }

  groups.push({
    label: "Sales & stock",
    entries: [
      { to: "/stock", label: "Stock", Icon: Egg },
      ...(notReadOnly ? [
        { to: "/customers", label: "Customers", Icon: Users },
        { to: "/sales", label: "Sales", Icon: ShoppingCart },
      ] : []),
      { to: "/history", label: "History", Icon: History },
    ],
  });

  groups.push({
    label: "Insights",
    entries: [
      { to: "/reports", label: "Reports", Icon: ChartColumn },
      ...(isAdmin ? [{ to: "/expenses", label: "Expenses", Icon: Wallet }] : []),
    ],
  });

  if (isAdmin) {
    groups.push({
      label: "Setup",
      entries: [
        { to: "/grades", label: "Grades", Icon: Tags },
        { to: "/products", label: "Products", Icon: Package },
        ...(role === "Admin" ? [{ to: "/users", label: "Users", Icon: UserCog }] : []),
        { to: "/audit", label: "Audit", Icon: ScrollText },
        { to: "/export", label: "Export", Icon: Download },
      ],
    });
  }

  groups.push({ label: "Help", entries: [{ to: "/help", label: "Help", Icon: CircleHelp }] });

  return groups;
}

// Most-used destinations first. The bottom bar takes the first four of these
// the role can reach; a producer gets Daily entry, a ReadOnly viewer does not,
// and either way whatever is not a tab is one tap away in More.
const TAB_PRIORITY = [
  "/daily-entry", "/stock", "/sales", "/history", "/", "/reports", "/inventory", "/flocks", "/water",
];

// The four thumb tabs (More is the fixed fifth slot). Priority order wins;
// should a narrow role not fill four from the priority list, the rest come in
// group order so the bar is never short.
export function tabEntries(groups: NavGroup[]): NavEntry[] {
  const all = groups.flatMap((g) => g.entries);
  const byRoute = new Map(all.map((e) => [e.to, e]));
  const picked: NavEntry[] = [];
  const take = (e: NavEntry | undefined) => {
    if (e && !picked.includes(e) && picked.length < 4) picked.push(e);
  };

  for (const route of TAB_PRIORITY) take(byRoute.get(route));
  for (const e of all) take(e); // backfill, in case priority left it short
  return picked;
}
