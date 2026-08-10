import {
  Bird, Boxes, ChartColumn, CircleHelp, ClipboardList, Download, Egg, History,
  LayoutDashboard, Package, ScrollText, Settings, ShoppingCart, Tags, UserCog,
  UserRound, Users, Wallet, Droplets, Wheat,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import type { Role } from "../auth/claims";
import type { Resources } from "../i18n/en";

// A key into the `nav` i18next namespace (#182, Task 7). This file is a PURE
// FUNCTION module — it cannot call useTranslation — so nav items carry a
// labelKey instead of English text; AppLayout and BottomNav translate it at
// their render sites via `t(item.labelKey)`.
export type NavLabelKey = keyof Resources["nav"];

// One nav model, three renderers (sidebar, bottom tabs, the More sheet). The
// role gates live HERE only — a second copy in the mobile nav would be the
// exact drift that lets a ReadOnly user see a Sales tab the API then rejects.
export interface NavEntry {
  to: string;
  labelKey: NavLabelKey;
  Icon: LucideIcon;
  end?: boolean;
}

export interface NavGroup {
  labelKey: NavLabelKey;
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
    { labelKey: "groupOverview", entries: [{ to: "/", labelKey: "dashboard", Icon: LayoutDashboard, end: true }] },
  ];

  if (canProduce) {
    groups.push({
      labelKey: "groupProduction",
      entries: [
        { to: "/daily-entry", labelKey: "dailyEntry", Icon: ClipboardList },
        { to: "/flocks", labelKey: "flocks", Icon: Bird },
        { to: "/feed", labelKey: "feed", Icon: Wheat },
        { to: "/water", labelKey: "water", Icon: Droplets },
        { to: "/inventory", labelKey: "inventory", Icon: Boxes },
      ],
    });
  }

  groups.push({
    labelKey: "groupSalesStock",
    entries: [
      { to: "/stock", labelKey: "stock", Icon: Egg },
      ...(notReadOnly ? [
        { to: "/customers", labelKey: "customers" as const, Icon: Users },
        { to: "/sales", labelKey: "sales" as const, Icon: ShoppingCart },
      ] : []),
      { to: "/history", labelKey: "history", Icon: History },
    ],
  });

  groups.push({
    labelKey: "groupInsights",
    entries: [
      { to: "/reports", labelKey: "reports", Icon: ChartColumn },
      ...(isAdmin ? [{ to: "/expenses", labelKey: "expenses" as const, Icon: Wallet }] : []),
    ],
  });

  if (isAdmin) {
    groups.push({
      labelKey: "groupSetup",
      entries: [
        // Same gate as the API's /account/settings (AdminOnly = Owner or
        // Manager), not the narrower Users one.
        { to: "/settings", labelKey: "farmSettings", Icon: Settings },
        { to: "/grades", labelKey: "grades", Icon: Tags },
        { to: "/products", labelKey: "products", Icon: Package },
        ...(role === "Admin" ? [{ to: "/users", labelKey: "users" as const, Icon: UserCog }] : []),
        { to: "/audit", labelKey: "audit", Icon: ScrollText },
        { to: "/export", labelKey: "export", Icon: Download },
      ],
    });
  }

  // #165 — every role can change their own password, so Account is ungated.
  groups.push({ labelKey: "groupYou", entries: [{ to: "/account", labelKey: "account", Icon: UserRound }] });

  groups.push({ labelKey: "groupHelp", entries: [{ to: "/help", labelKey: "help", Icon: CircleHelp }] });

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
