#!/usr/bin/env bash
# Capture four 1:1 frames of one route from the RUNNING simulation stack:
# 1280x800 and 390x844, light and dark, into /tmp/<slug>/after-<w>-<theme>.png.
# Reads the stack; never resets it. Signs in as the farm's owner from .sim-cast.json.
#
#   .claude/skills/verify/capture.sh <slug> [route=/] [farm=readme|default] [prefix=after]
set -euo pipefail

slug="${1:?slug, e.g. 883-after}"
route="${2:-/}"
farm="${3:-readme}"
prefix="${4:-after}"

# The four arguments are interpolated into a generated spec and a /tmp path, so
# each is held to a literal alphabet: no quotes, no braces, no path separators
# in the names, and the route is an absolute app path with a query at most.
name_re='^[A-Za-z0-9._-]+$'
route_re='^/[A-Za-z0-9._/?=&-]*$'
[[ "$slug" =~ $name_re ]] || { echo "slug must match $name_re" >&2; exit 2; }
[[ "$prefix" =~ $name_re ]] || { echo "prefix must match $name_re" >&2; exit 2; }
[[ "$route" =~ $route_re ]] || { echo "route must be an absolute app path like /daily-entry" >&2; exit 2; }

root="$(git rev-parse --show-toplevel)"
ui="$root/tools/simulation/ui"
out="/tmp/$slug"
spec="$ui/specs-screenshots/verify-capture-$slug-screenshots.spec.ts"

case "$farm" in
  readme) persona="readmeFarmOwner()" ;;
  default) persona="owner()" ;;
  *) echo "farm must be readme or default" >&2; exit 2 ;;
esac

[ -f "$root/tools/simulation/.sim-cast.json" ] || { echo "no .sim-cast.json in $root: run tools/simulation/bootstrap.sh" >&2; exit 1; }
curl -fsS -o /dev/null http://127.0.0.1:8081/health/ready || { echo "stack not ready on 127.0.0.1:8081" >&2; exit 1; }
[ -d "$ui/node_modules" ] || (cd "$ui" && npm ci --silent)

mkdir -p "$out"
cleanup() { rm -f "$spec"; }
trap cleanup EXIT

cat > "$spec" <<EOF
import { test } from "../src/fixtures";
import { owner, readmeFarmOwner } from "../src/cast";

const OUT = "$out/";
const ROUTE = "$route";
const SHOTS: Array<[number, number, "light" | "dark"]> = [
  [1280, 800, "light"], [1280, 800, "dark"], [390, 844, "light"], [390, 844, "dark"],
];

test.describe("verify capture $slug", () => {
  for (const [w, h, theme] of SHOTS) {
    test(\`verify-capture \${w} \${theme}\`, async ({ page, signIn }) => {
      const errors: string[] = [];
      page.on("pageerror", (e) => errors.push(String(e)));
      page.on("console", (m) => { if (m.type() === "error") errors.push(m.text()); });
      await page.setViewportSize({ width: w, height: h });
      await page.emulateMedia({ colorScheme: theme });
      await signIn($persona);
      await page.goto(ROUTE);
      await page.evaluate((t) => { document.documentElement.dataset.theme = t; }, theme);
      await page.waitForTimeout(800);
      await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
      await page.screenshot({ path: \`\${OUT}$prefix-\${w}-\${theme}.png\`, animations: "disabled" });
      if (errors.length) console.log(\`[console errors at \${w} \${theme}] \` + errors.join(" | "));
    });
  }
});
EOF

(cd "$ui" && npx playwright test --config playwright.screenshots.config.ts --grep "verify-capture" --project=chromium 2>&1 | grep -E "passed|failed|console errors" || true)
ls -1 "$out"
