---
name: verify
description: Launch, doctor, drive and capture evidence from the real Cluckwork app (the .NET API serving the built React SPA) to verify a change end-to-end in a real browser. Reach for it when a PR changes what a user sees or does, before claiming a UI change works, and for the before/after screenshots AGENTS.md requires.
---

# Verifying Cluckwork at runtime

Cluckwork's user surface is the web SPA (`web/`) served by the API. The primary way to
verify it is the **simulation stack**: a Production-config app plus Postgres and Redis under
the `cluckwork-sim` compose project, seeded with a deterministic fixture and driven by the
Playwright suite in `tools/simulation/ui/`. Every revamp PR in September 2026 was verified
this way. The dev-server path (Compose Postgres, `dotnet run`, Vite) still exists for
debugging and is described at the end; it is not the evidence path.

Read `features/README.md` for the per-feature map before driving anything: a proof that
takes one convenient entry point is incomplete when the map lists others.

## Launch

Run these in the repo root or in a worktree of the branch under review (the stack is built from the
checkout you run this in, so a worktree at the PR head captures the PR head):

```sh
bash tools/simulation/bootstrap.sh            # once per checkout: writes .env.sim + .sim-cast.json (git-ignored)
sg docker -c 'bash tools/simulation/reset.sh' # wipe, rebuild the image, migrate, seed both farms, self-check (about 4-6 min)
```

`sg docker` is required on this machine: the shell's group list predates the docker group
(see the global CLAUDE.md note). Ready is the last three lines of `reset.sh`'s output:

```
Manifest: .../tools/simulation/out/manifest.json
Cast:     .../tools/simulation/.sim-cast.json
Farms:    default-farm (simulation fixture), readme-farm (demo, README capture)
```

Run it in the background and poll for the `Farms:` line with an `until` loop in a
foreground call; never end a turn to wait on it.

**Two farms come up.** `default-farm` is the simulation fixture: about 100 catalog flocks
that never file, so the Dashboard's Today list is capped at twelve unrecorded rows and the
fortnight strip has no complete day. `readme-farm` ("Meadowlark") is the demo profile: two
houses, one seeded as today's draft, real orders. **Capture screenshots on `readme-farm`**;
drive write flows on `default-farm`, which the smoke specs already do.

**Isolation: there is exactly one stack.** Both farms live in one compose project on
`127.0.0.1:8081`, and `reset.sh` runs `down -v` on it. Never run `reset.sh` while another
agent is driving or capturing; check with the doctor first and coordinate through the
coordinator. Reading the running stack (a capture, a smoke run) is safe to share; a reset
is not.

## Doctor

Run this before driving whenever anything looks off. All four answers must be yes.

```sh
sg docker -c 'docker ps --filter name=cluckwork-sim --format "{{.Names}} {{.Status}}"'   # app, db, redis healthy
curl -fsS -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8081/health/ready              # 200
ls tools/simulation/.sim-cast.json tools/simulation/.env.sim                              # both present in THIS checkout
git rev-parse --short HEAD                                                                # the commit the image was built from is this checkout's HEAD
```

The fourth line is the one people skip: a long-running stack serves the bytes it was built
from, not the branch you are looking at. If the stack predates your head, reset.

## Drive

Use the harness, not a scratch `playwright-core` script: its fixtures sign in through the
real login form with credentials from `.sim-cast.json`, respect the farm clock, and run in
two projects (`chromium` at 1280x720, `chromium-phone` at 390x844, partitioned by the
`@phone` tag).

```sh
cd tools/simulation/ui
npm test                                              # the whole smoke suite, about 30 s
npx playwright test specs/owner.spec.ts --project=chromium            # one spec
npx playwright test specs/phone.spec.ts --project=chromium-phone      # the phone project
```

Inside a spec, the handles that matter:

- `import { test, expect } from "../src/fixtures"` gives `page`, `signIn(member)`, `farm`
  (timezone), `nav` (desktop shell), `phone` (tab bar and More sheet at 390).
- Personas from `../src/cast`: `owner()`, `castMember("Manager")`,
  `unrestrictedWorker()`, `restrictedWorker()`, `readmeFarmOwner()` (the demo farm; its
  `farmCode` is `readme-farm`). Never type a credential; never print one.
- Labels through `tEn("<ns>:<key>")` from `../src/i18n`, never hardcoded English:
  `tEn("dailyEntry:flockLabel")`, `tEn("sales:newOrder")`, `tEn("stock:lotsHeading")`.
- `commitNamedPicker(page, tEn("dailyEntry:flockLabel"), "Sim House A")` from `../src/dom`
  drives the searchable picker; `selectOptionContaining(select, needle)` for plain selects.
- Anything about `inert` or the accessibility tree goes through CDP in `../src/ax.ts`;
  Playwright's own APIs do not model `inert` (#501).
- Rows on the new Dashboard are `getByRole("group", { name: <flock name> })`; the sales
  list is `getByRole("list", { name: tEn("dashboard:salesPanelTitle") })`; footer actions
  are buttons by their catalog label.

A throwaway spec for a one-off proof goes under `specs-screenshots/` with a name ending in
`-screenshots.spec.ts` (that config's `testMatch`) and is deleted before you commit; the
`capture.sh` helper below writes and removes one for you.

## Evidence

Proof standards, in order of how often they are skipped:

1. **Exercise the real user path.** Sign in through the form, navigate by the nav or the
   URL, click the control the user clicks. No test-only endpoints, no internal setters.
2. **Capture the action and the resulting state**, not only the final screen: a Draft row
   after saving, the stock lot after a write-off, the order after a payment.
3. **Verify side effects** alongside the pixels: the API's row through
   `GET /api/v1/...` with the persona's token, or the next screen that reads it (History for
   an adjusted entry, Stock for an allocated order).
4. **Screenshots are 1:1**: viewport only, device scale factor 1, no full-page smears of
   sticky bars, 1280x800 and 390x844, light and dark, from a stack rebuilt at the head under
   review. Before frames come from `origin/main` rebuilt the same way.
5. **Where it goes**: `/tmp/<slug>/{before,after}-<width>-<theme>.png`. Attach to the PR with
   `gh pr comment <N> -R mforce/cluckwork --attach './after-1280-light.png#After, 1280 light'`;
   never commit images to a branch (a branch is deletable and takes the evidence with it).
   Also record console errors (`page.on("pageerror")`, CSP refusals) in the PR comment when
   they appear; `csp-nonce.spec.ts` shows the assertion.

`capture.sh` does 4 and writes the files for 5:

```sh
.claude/skills/verify/capture.sh <slug> [route] [farm] [prefix]
# examples
.claude/skills/verify/capture.sh 883-after /            readme          # Dashboard, four frames
.claude/skills/verify/capture.sh 888-after /daily-entry readme
.claude/skills/verify/capture.sh 888      /daily-entry readme before   # on a stack built from main
```

It signs in as the named farm's owner, sets the theme through `data-theme`, captures the
four frames into `/tmp/<slug>/`, prints any console errors it saw, and removes its
throwaway spec. It reads the running stack; it never resets it. One `401 (Unauthorized)`
resource error per run is the sign-in page's session probe on a fresh browser context,
observed on every proof run against a healthy stack; anything else in that line is a
finding to report.

## Cleanup

- Remove any throwaway spec you wrote under `specs-screenshots/` and any scratch files in
  the worktree (`git status --short` must be empty of your leftovers before the commit).
- Leave the stack up unless you started it for yourself and nobody else is driving it. To
  take it down: `sg docker -c 'docker compose -p cluckwork-sim down -v'` from the checkout
  that started it. Never kill by process name.
- Evidence survives cleanup: `/tmp/<slug>/` stays, and the attached PR comment is the
  durable copy.

## The dev-server path (debugging, not evidence)

Useful when you need hot reload or a debugger, not for proofs. First check that no Aspire
AppHost owns the ports (`pgrep -af 'Cluckwork\.AppHost'`, `ss -ltn | grep ':18888\b'`, or
`aspire describe --apphost src/Cluckwork.AppHost/Cluckwork.AppHost.csproj --format Json --non-interactive`);
Aspire's Postgres is a different database with a generated credential (#565), so do not
run both.

```sh
docker compose -f deploy/docker-compose.dev.yml up -d --wait                    # Postgres 5432
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:8080 \
  dotnet run --project src/Cluckwork.Api --no-launch-profile                     # API; the env var is load-bearing
cd web && npm run dev                                                            # Vite on 5173, proxies /api
```

A fresh database has no Owner: provision one per
`docs/runbooks/first-admin-provisioning.md` and use that printed password. Seeded Compose
credentials: `dotnet user-secrets list --project src/Cluckwork.Api | grep '^Seed:'`. API
writes need `Authorization: Bearer` and an `Idempotency-Key: $(uuidgen)` header.
Teardown: stop the two processes you started, then
`docker compose -f deploy/docker-compose.dev.yml down`.
