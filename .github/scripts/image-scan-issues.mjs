// Turns the weekly image scan's Trivy JSON reports into GitHub issues: one issue
// per vulnerability id, ever.
//
//   node image-scan-issues.mjs --repo o/r --sha <40 hex> --image <ref> \
//     --run-url <url> --report amd64=<json> --report arm64=<json> [--dry-run]
//
// Closing an issue IGNORES its vulnerability; reopening it stops ignoring it.
// Per vulnerability found:
//   - an open issue for the id gets a comment (and loses the `ignored` label if it
//     has one, because reopening is how an owner un-ignores it);
//   - else a closed issue for the id is left alone apart from getting the
//     `ignored` label once; it is neither reopened nor commented on, so a CVE
//     that returns after being fixed is not re-reported;
//   - else a new issue is created.
// Per scanner issue whose id is NOT found: an open one is commented on and closed
// because the image no longer carries it, and a closed one loses `ignored`, which
// means "still present, deliberately ignored".
//
// "Not found" is only trustworthy if the scan ran. Both architecture reports must
// parse and name the image they were asked to scan, or this exits 1 before it
// reads or writes any issue.
//
// An issue matches an id by a title of the exact shape buildTitle writes, open or
// closed, whoever filed it. An issue is only CLOSED when it is also authored by the workflow's bot
// and carries both labels, so a human-filed issue is never closed. Issue bodies
// are never matched: a human issue that merely mentions an id must not be taken
// for its tracking issue.
//
// --dry-run reads the live issue list and prints what it would do. --issues
// substitutes a JSON array of {number,title,state,labels:[name],author,comments:[body]}
// for that list, to exercise the decision logic without the live tracker.

import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

export const ARCHS = ["amd64", "arm64"];
export const LABELS = ["dependencies", "docker"];
export const IGNORED = "ignored";
const IGNORED_DESCRIPTION = "Vulnerability still present, deliberately ignored: reopen the issue to un-ignore";

const RANK = { HIGH: 1, CRITICAL: 2 };
const ID = /^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$/;
const TITLE = /^\[(CRITICAL|HIGH)\] (\S+) in .+ \(container image\)$/;

// Each architecture is scanned by its own manifest digest, so the report's
// ArtifactName is `<repo>@sha256:<child digest>` and names exactly what was scanned.
export function parseReport(text, { repo, arch }) {
  let report;
  try {
    report = JSON.parse(text);
  } catch (err) {
    throw new Error(`${arch} report is not JSON: ${err.message}`);
  }
  const meta = report?.Metadata;
  if (report?.SchemaVersion !== 2 || report.ArtifactType !== "container_image") {
    throw new Error(`${arch} report is not a Trivy container-image report`);
  }
  const digest = report.ArtifactName?.startsWith(`${repo}@`) ? report.ArtifactName.slice(repo.length + 1) : "";
  if (!/^sha256:[0-9a-f]{64}$/.test(digest)) {
    throw new Error(`${arch} report scanned ${report.ArtifactName}, not a digest of ${repo}`);
  }
  if (meta?.ImageConfig?.architecture !== arch) {
    throw new Error(`${arch} report scanned a ${meta?.ImageConfig?.architecture} image`);
  }
  if (!meta.OS?.Family) {
    throw new Error(`${arch} report detected no operating system, so the scan did not run`);
  }
  const vulns = [];
  for (const result of report.Results ?? []) {
    for (const v of result.Vulnerabilities ?? []) {
      if (!ID.test(v.VulnerabilityID ?? "") || !(v.Severity in RANK)) continue;
      vulns.push({
        id: v.VulnerabilityID,
        severity: v.Severity,
        pkg: v.PkgName,
        installed: v.InstalledVersion,
        fixed: v.FixedVersion ?? "",
        summary: String(v.Title ?? "").replace(/\s+/g, " ").trim(),
        url: v.PrimaryURL ?? "",
      });
    }
  }
  return { digest, vulns };
}

export function collect(parsed) {
  const findings = new Map();
  for (const [arch, { vulns }] of Object.entries(parsed)) {
    for (const v of vulns) {
      const f = findings.get(v.id) ?? { id: v.id, severity: v.severity, summary: v.summary, url: v.url, rows: new Map() };
      if (RANK[v.severity] > RANK[f.severity]) f.severity = v.severity;
      const key = `${v.pkg}|${v.installed}|${v.fixed}`;
      const row = f.rows.get(key) ?? { pkg: v.pkg, installed: v.installed, fixed: v.fixed, archs: new Set() };
      row.archs.add(arch);
      f.rows.set(key, row);
      findings.set(v.id, f);
    }
  }
  return findings;
}

const packages = (f) => [...new Set([...f.rows.values()].map((r) => r.pkg))].sort();
const archsOf = (f) => ARCHS.filter((a) => [...f.rows.values()].some((r) => r.archs.has(a)));

export const buildTitle = (f) => `[${f.severity}] ${f.id} in ${packages(f).join(", ")} (container image)`;

export const scannerIssueId = (title) => TITLE.exec(title)?.[2] ?? null;

const SCANNER_AUTHOR = "github-actions";
const isScanner = (issue) =>
  scannerIssueId(issue.title) !== null &&
  LABELS.every((l) => issue.labels.includes(l)) &&
  issue.author?.replace(/^app\//, "").replace(/\[bot\]$/, "") === SCANNER_AUTHOR;

const marker = (kind, sha) => `<!-- image-scan ${kind} sha=${sha} -->`;
// Text from the vulnerability database goes into issue bodies. Escape the table
// delimiters (backslash first) and break @mentions so it cannot ping anyone.
const md = (s) => String(s).replace(/\s+/g, " ").replace(/\\/g, "\\\\").replace(/\|/g, "\\|").replace(/@/g, "@\u200b");

function createBody(f, ctx) {
  const rows = [...f.rows.values()]
    .sort((a, b) => a.pkg.localeCompare(b.pkg))
    .map((r) => `| ${md(r.pkg)} | ${md(r.installed)} | ${md(r.fixed)} | ${ARCHS.filter((a) => r.archs.has(a)).join(", ")} |`);
  return [
    `The weekly image scan found **${f.id}** (${f.severity}) in \`${ctx.ref}\`.`,
    "",
    `${md(f.summary)}${/^https:\/\/[^\s<>]+$/.test(f.url) ? ` (<${f.url}>)` : ""}`,
    "",
    "| Package | Installed | Fixed | Architectures |",
    "| --- | --- | --- | --- |",
    ...rows,
    "",
    `Scan run: ${ctx.runUrl}`,
    "",
    `This is the single issue for ${f.id}. The scan comments here while the image still carries it and closes this issue once a scan no longer finds it. Close it by hand to ignore ${f.id}: the scan then leaves it alone and labels it \`${IGNORED}\`. Reopen it to stop ignoring.`,
    "",
    marker("found", ctx.sha),
  ].join("\n");
}

const foundComment = (f, ctx) =>
  [
    `Weekly image scan: ${f.id} is still present in \`sha-${ctx.sha}\` on ${archsOf(f).map((a) => `linux/${a}`).join(", ")}.`,
    "",
    `Scan run: ${ctx.runUrl}`,
    "",
    marker("found", ctx.sha),
  ].join("\n");

const clearedComment = (id, ctx) =>
  [
    `Weekly image scan: ${id} is no longer reported as a fixable HIGH or CRITICAL finding on linux/amd64 or linux/arm64 as of \`sha-${ctx.sha}\`. Closing.`,
    "",
    "Scanned manifests:",
    ...ARCHS.map((a) => `- linux/${a}: \`${ctx.digests[a]}\``),
    "",
    `Scan run: ${ctx.runUrl}`,
  ].join("\n");

// Pure decision logic. `issues` are every scanner-visible issue, open and closed,
// each `{number, title, state, labels, author, comments}`; the caller has already
// refused to get here unless both scans completed.
export function plan(findings, issues, ctx) {
  const scanner = issues.filter((i) => scannerIssueId(i.title) !== null);
  const matches = (id, state) =>
    scanner.filter((i) => scannerIssueId(i.title) === id && i.state === state).sort((a, b) => a.number - b.number);
  const actions = [];
  const sorted = [...findings.values()].sort(
    (a, b) => RANK[b.severity] - RANK[a.severity] || a.id.localeCompare(b.id),
  );
  for (const f of sorted) {
    const [open] = matches(f.id, "open");
    const closed = matches(f.id, "closed");
    if (open) {
      if (open.labels.includes(IGNORED)) actions.push({ kind: "unlabel", id: f.id, number: open.number });
      if ((open.comments ?? []).some((c) => c.includes(marker("found", ctx.sha)))) {
        actions.push({ kind: "skip", id: f.id, number: open.number, why: `#${open.number} already notes sha-${ctx.sha}` });
      } else {
        actions.push({ kind: "comment", id: f.id, number: open.number, body: foundComment(f, ctx) });
      }
    } else if (closed.length > 0) {
      for (const c of closed) {
        actions.push(
          c.labels.includes(IGNORED)
            ? { kind: "skip", id: f.id, number: c.number, why: `#${c.number} is closed and already ignored` }
            : { kind: "label", id: f.id, number: c.number },
        );
      }
    } else {
      actions.push({ kind: "create", id: f.id, title: buildTitle(f), body: createBody(f, ctx), labels: LABELS });
    }
  }
  for (const i of scanner.filter((x) => !findings.has(scannerIssueId(x.title)))) {
    const id = scannerIssueId(i.title);
    if (i.state === "open" && isScanner(i)) {
      actions.push({ kind: "close", id, number: i.number, body: clearedComment(id, ctx) });
      if (i.labels.includes(IGNORED)) actions.push({ kind: "unlabel", id, number: i.number });
    } else if (i.state === "closed" && i.labels.includes(IGNORED)) {
      actions.push({ kind: "unlabel", id, number: i.number });
    }
  }
  return actions;
}

function gh(args, input) {
  return execFileSync("gh", args, { encoding: "utf8", input, stdio: ["pipe", "pipe", "inherit"] });
}

// Every issue, open and closed. The REST list also returns pull requests, which
// carry a `pull_request` key; --paginate follows every page.
function allIssues(repo) {
  const out = gh([
    "api", "--paginate", `repos/${repo}/issues?state=all&per_page=100`,
    "--jq", ".[] | select(has(\"pull_request\") | not) | {number, title, state, labels: [.labels[].name], author: .user.login}",
  ]);
  return out.split("\n").filter(Boolean).map((line) => JSON.parse(line));
}

function commentsOf(repo, number) {
  return JSON.parse(gh(["issue", "view", String(number), "-R", repo, "--json", "comments"])).comments.map((c) => c.body);
}

function ensureIgnoredLabel(repo) {
  gh(["label", "create", IGNORED, "-R", repo, "--force", "--color", "6e7681", "--description", IGNORED_DESCRIPTION]);
}

function apply(repo, a) {
  if (a.kind === "label" || a.kind === "unlabel") {
    return gh(["issue", "edit", String(a.number), "-R", repo, a.kind === "label" ? "--add-label" : "--remove-label", IGNORED]).trim();
  }
  if (a.kind === "create") {
    const args = ["issue", "create", "-R", repo, "--title", a.title, "--body-file", "-"];
    for (const l of a.labels) args.push("--label", l);
    return gh(args, a.body).trim();
  }
  if (a.kind === "comment") return gh(["issue", "comment", String(a.number), "-R", repo, "--body-file", "-"], a.body).trim();
  return gh(["issue", "close", String(a.number), "-R", repo, "--reason", "completed", "--comment", a.body]).trim();
}

const describe = (a, dry) => {
  const v = (live, would) => (dry ? would : live);
  if (a.kind === "create") return `${v("create", "would create")} a new issue for ${a.id}: ${a.title}`;
  if (a.kind === "comment") return `${v("update", "would update")} #${a.number} for ${a.id} with a comment`;
  if (a.kind === "close") return `${v("close", "would close")} #${a.number} (${a.id} no longer found)`;
  if (a.kind === "label") return `${v("add", "would add")} the ${IGNORED} label to closed #${a.number} (${a.id} still present, ignored)`;
  if (a.kind === "unlabel") return `${v("remove", "would remove")} the ${IGNORED} label from #${a.number} (${a.id})`;
  return `skip ${a.id}: ${a.why}`;
};

function parseArgs(argv) {
  const o = { reports: {}, dryRun: false };
  for (let i = 0; i < argv.length; i++) {
    const k = argv[i];
    if (k === "--dry-run") o.dryRun = true;
    else if (k === "--report") {
      const [arch, path] = argv[++i].split("=");
      o.reports[arch] = path;
    } else if (["--repo", "--sha", "--image", "--run-url", "--issues"].includes(k)) {
      o[k.slice(2).replace(/-(\w)/g, (_, c) => c.toUpperCase())] = argv[++i];
    } else throw new Error(`unknown argument ${k}`);
  }
  for (const k of ["repo", "sha", "image", "runUrl"]) if (!o[k]) throw new Error(`--${k} is required`);
  if (!/^[0-9a-f]{40}$/.test(o.sha)) throw new Error("--sha must be a full commit sha");
  return o;
}

function main() {
  const o = parseArgs(process.argv.slice(2));
  const parsed = {};
  for (const arch of ARCHS) {
    if (!o.reports[arch]) throw new Error(`no ${arch} report: refusing to act on a partial scan`);
    parsed[arch] = parseReport(readFileSync(o.reports[arch], "utf8"), { repo: o.image.replace(/:[^:/]+$/, ""), arch });
  }
  const ctx = {
    sha: o.sha,
    ref: o.image,
    runUrl: o.runUrl,
    digests: Object.fromEntries(ARCHS.map((a) => [a, parsed[a].digest])),
  };
  const issues = o.issues ? JSON.parse(readFileSync(o.issues, "utf8")) : allIssues(o.repo);
  for (const i of issues) {
    i.state ??= "open";
    if (i.state === "open" && scannerIssueId(i.title) !== null && !i.comments) {
      i.comments = o.issues ? [] : commentsOf(o.repo, i.number);
    }
  }
  const actions = plan(collect(parsed), issues, ctx);
  console.log(`image-scan: ${ctx.ref} scanned on ${ARCHS.join(" and ")}; ${actions.length} action(s)${o.dryRun ? " (dry run)" : ""}`);
  let labelEnsured = false;
  for (const a of actions) {
    console.log(describe(a, o.dryRun));
    if (o.dryRun || a.kind === "skip") continue;
    if (a.kind === "label" && !labelEnsured) {
      ensureIgnoredLabel(o.repo);
      labelEnsured = true;
    }
    console.log(`  ${apply(o.repo, a)}`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    main();
  } catch (err) {
    console.error(`image-scan-issues: ${err.message}`);
    process.exit(1);
  }
}
