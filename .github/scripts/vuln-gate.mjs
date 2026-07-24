#!/usr/bin/env node
// Dependency-vulnerability gate for CI (#146).
//
// Why a script and not the stock commands:
//   - `npm audit --audit-level=high` gates, but has NO allowlist. One unfixable
//     upstream advisory would block every unrelated PR until it is patched.
//   - `dotnet list package --vulnerable` ALWAYS exits 0, so on its own it cannot
//     gate anything — its output has to be parsed either way.
// Both sides need parsing, so parsing once buys a single exceptions file, one
// severity ladder, and one report format across the two ecosystems.
//
// Usage:
//   npm audit --json --omit=dev | node .github/scripts/vuln-gate.mjs --ecosystem npm
//   dotnet list package --vulnerable --include-transitive --format json \
//     | node .github/scripts/vuln-gate.mjs --ecosystem nuget
//
// Flags: --level <low|moderate|high|critical>  (default high — blocks at or above)
//        --warn-only                           (report, always exit 0)
//        --exceptions <path>                   (default .github/security-exceptions.json)
//
// Exit 0 = clean or fully excepted; exit 1 = blocking advisory; exit 2 = bad input.

import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

const SEVERITIES = ["info", "low", "moderate", "high", "critical"];

export function severityRank(severity) {
  const i = SEVERITIES.indexOf(String(severity ?? "").toLowerCase());
  return i < 0 ? 0 : i;
}

// Both ecosystems point their advisory URLs at GitHub Security Advisories, so a
// GHSA id is the one key that means the same thing on both sides — that is what
// an exception is written against. Numeric npm ids / package coordinates are
// only a fallback for an advisory published somewhere else.
const GHSA = /(GHSA-[0-9a-z]{4}-[0-9a-z]{4}-[0-9a-z]{4})/i;

export function advisoryId(url, fallback) {
  const match = typeof url === "string" ? url.match(GHSA) : null;
  // Canonical GitHub form: uppercase prefix, lowercase body — so an id pasted
  // straight from a GitHub advisory page is what appears in the log.
  return match ? `GHSA-${match[1].slice(5).toLowerCase()}` : String(fallback ?? "UNKNOWN");
}

function dedupe(findings) {
  const seen = new Map();
  for (const f of findings) {
    const key = `${f.id}|${f.package}`;
    // Keep the worst severity reported for the same advisory/package pair.
    const prev = seen.get(key);
    if (!prev || severityRank(f.severity) > severityRank(prev.severity)) seen.set(key, f);
  }
  return [...seen.values()];
}

// `npm audit --json` (auditReportVersion 2). Each vulnerabilities[name].via entry
// is either an advisory object or a bare package name — the latter means "this
// package is vulnerable *through* that one", and the advisory itself is listed
// on that other package's own entry, so counting it here would double-report.
export function parseNpm(report) {
  const findings = [];
  for (const entry of Object.values(report?.vulnerabilities ?? {})) {
    for (const via of entry?.via ?? []) {
      if (typeof via !== "object" || via === null) continue;
      findings.push({
        id: advisoryId(via.url, via.source),
        package: via.name ?? entry.name ?? "unknown",
        severity: String(via.severity ?? entry.severity ?? "").toLowerCase(),
        title: via.title ?? "",
        url: via.url ?? "",
      });
    }
  }
  return dedupe(findings);
}

// `dotnet list package --vulnerable --include-transitive --format json`. A clean
// project has no `frameworks` key at all, so every level is optional. NuGet spells
// the advisory link `advisoryurl` (all lower case); accept the camelCase spelling
// too in case that is ever normalised.
export function parseNuget(report) {
  const findings = [];
  for (const project of report?.projects ?? []) {
    for (const framework of project?.frameworks ?? []) {
      for (const kind of ["topLevelPackages", "transitivePackages"]) {
        for (const pkg of framework?.[kind] ?? []) {
          for (const vuln of pkg?.vulnerabilities ?? []) {
            const url = vuln.advisoryurl ?? vuln.advisoryUrl ?? "";
            const coordinates = `${pkg.id}@${pkg.resolvedVersion ?? "?"}`;
            findings.push({
              id: advisoryId(url, coordinates),
              package: coordinates,
              severity: String(vuln.severity ?? "").toLowerCase(),
              title: kind === "transitivePackages" ? "transitive" : "direct",
              url,
            });
          }
        }
      }
    }
  }
  return dedupe(findings);
}

// An exception without a parseable `expires` never suppresses anything. That is
// deliberate: an exception with no end date is an indefinite hole, and the whole
// point of the file is that muting an advisory stays a dated, revisited decision.
function isLive(exception, now) {
  const expires = Date.parse(exception?.expires ?? "");
  return Number.isFinite(expires) && expires > now.getTime();
}

function matches(exception, finding, ecosystem) {
  const scope = String(exception?.ecosystem ?? "any").toLowerCase();
  if (scope !== "any" && scope !== ecosystem) return false;
  return String(exception?.id ?? "").toUpperCase() === finding.id.toUpperCase();
}

export function gate({ findings, exceptions = [], ecosystem, level = "high", now = new Date() }) {
  const floor = severityRank(level);
  const atOrAbove = findings.filter((f) => severityRank(f.severity) >= floor);

  const suppressed = [];
  const blocking = [];
  for (const finding of atOrAbove) {
    const excuse = exceptions.find((e) => matches(e, finding, ecosystem) && isLive(e, now));
    if (excuse) suppressed.push({ ...finding, reason: excuse.reason ?? "", expires: excuse.expires });
    else blocking.push(finding);
  }

  // Surfaced so a lapsed entry gets deleted instead of lingering as dead config.
  const staleExceptions = exceptions.filter(
    (e) => (String(e?.ecosystem ?? "any").toLowerCase() === "any"
      || String(e?.ecosystem).toLowerCase() === ecosystem) && !isLive(e, now),
  );

  return {
    blocking,
    suppressed,
    staleExceptions,
    belowLevel: findings.length - atOrAbove.length,
  };
}

// The live (unexpired) GHSA ids, for actions/dependency-review-action's
// `allow-ghsas` input — so the diff-scoped PR gate honours the very same
// exceptions file as the tree-scoped audit gates, from one source of truth.
// Ids without a GHSA (numeric-only npm ids, bare coordinates) can't be expressed
// to that action and are dropped here; they still work for the audit gates.
export function emitAllowlist(exceptions, now = new Date()) {
  return exceptions
    .filter((e) => isLive(e, now))
    .map((e) => String(e.id ?? ""))
    .filter((id) => /^GHSA-/i.test(id))
    .join(",");
}

function describe(finding) {
  const bits = [finding.severity, finding.package, finding.id, finding.title, finding.url];
  return bits.filter(Boolean).join(" — ");
}

export function report({ result, ecosystem, level, warnOnly, log = console.log }) {
  const kind = warnOnly ? "warning" : "error";
  for (const finding of result.blocking) log(`::${kind}::[${ecosystem}] ${describe(finding)}`);
  for (const finding of result.suppressed)
    log(`::notice::[${ecosystem}] excepted until ${finding.expires}: ${describe(finding)} — ${finding.reason}`);
  for (const stale of result.staleExceptions)
    log(`::warning::[${ecosystem}] exception ${stale.id} lapsed (${stale.expires ?? "no expiry"}) — remove it from .github/security-exceptions.json`);

  if (result.blocking.length === 0)
    log(`[${ecosystem}] no advisories at or above "${level}" (${result.suppressed.length} excepted, ${result.belowLevel} below threshold).`);
  else
    log(`[${ecosystem}] ${result.blocking.length} advisor${result.blocking.length === 1 ? "y" : "ies"} at or above "${level}"${warnOnly ? " (advisory only — not blocking)" : ""}.`);
}

export function parseArgs(argv) {
  const options = {
    ecosystem: "",
    level: "high",
    warnOnly: false,
    emitAllowlist: false,
    exceptionsPath: ".github/security-exceptions.json",
  };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--ecosystem") options.ecosystem = String(argv[++i] ?? "").toLowerCase();
    else if (argv[i] === "--level") options.level = String(argv[++i] ?? "").toLowerCase();
    else if (argv[i] === "--warn-only") options.warnOnly = true;
    else if (argv[i] === "--emit-allowlist") options.emitAllowlist = true;
    else if (argv[i] === "--exceptions") options.exceptionsPath = String(argv[++i] ?? "");
  }
  return options;
}

// The dotnet CLI can print restore chatter ahead of the JSON document, so start
// at the first brace rather than trusting the stream to be pure JSON.
export function extractJson(text) {
  const start = text.indexOf("{");
  if (start < 0) throw new Error("no JSON object found in input");
  return JSON.parse(text.slice(start));
}

async function readStdin() {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString("utf8");
}

function loadExceptions(path) {
  try {
    return JSON.parse(readFileSync(path, "utf8")).exceptions ?? [];
  } catch {
    return []; // no file → empty allowlist, the normal case
  }
}

async function main() {
  const options = parseArgs(process.argv.slice(2));

  // Allowlist mode: print the live GHSA ids for dependency-review and exit. No
  // stdin, no ecosystem — dependency-review spans both manifests at once.
  if (options.emitAllowlist) {
    process.stdout.write(emitAllowlist(loadExceptions(options.exceptionsPath)));
    return;
  }

  if (options.ecosystem !== "npm" && options.ecosystem !== "nuget") {
    console.error("usage: vuln-gate.mjs --ecosystem npm|nuget [--level high] [--warn-only]");
    process.exitCode = 2;
    return;
  }

  let report_;
  try {
    report_ = extractJson(await readStdin());
  } catch (err) {
    console.error(`::error::[${options.ecosystem}] could not parse the audit output: ${err.message}`);
    process.exitCode = 2; // a gate that cannot read its input must not pass silently
    return;
  }

  const exceptions = loadExceptions(options.exceptionsPath);
  const findings = options.ecosystem === "npm" ? parseNpm(report_) : parseNuget(report_);
  const result = gate({ findings, exceptions, ecosystem: options.ecosystem, level: options.level });
  report({ result, ecosystem: options.ecosystem, level: options.level, warnOnly: options.warnOnly });

  if (result.blocking.length > 0 && !options.warnOnly) process.exitCode = 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
