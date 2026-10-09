// #798 — where sign-in returns to. Only a path on this origin: anything that resolves
// elsewhere (an absolute URL, a protocol-relative //host, a backslash that a browser
// reads as a slash) goes home instead. The consent route keeps its query, which is the
// connected app's request; every other path returns without one, as before.
export function returnPath(from: { pathname: string; search?: string } | undefined): string {
  if (!from) return "/";
  const target = new URL(from.pathname + (from.search ?? ""), window.location.origin);
  if (target.origin !== window.location.origin) return "/";
  return target.pathname === "/connect" ? target.pathname + target.search : target.pathname;
}
