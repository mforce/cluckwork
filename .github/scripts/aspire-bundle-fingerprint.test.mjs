// Self-tests for the Aspire CLI bundle fingerprint check. Run with
// `node --test .github/scripts/aspire-bundle-fingerprint.test.mjs`.
//
// Each case builds a fake bundle home (versions/<id>/ plus a bundle symlink),
// records its fingerprint as the committed value, changes one thing, and
// asserts the check's exit status.

import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, renameSync, rmSync, symlinkSync, utimesSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, join } from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT = fileURLToPath(new URL("./aspire-bundle-fingerprint.sh", import.meta.url));
const PLATFORM = ["-s", "-m"]
  .map((flag) => spawnSync("uname", [flag], { encoding: "utf8" }).stdout.trim().toLowerCase())
  .join("-");

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), "aspire-bundle-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const versionDir = join(root, "home", "versions", "17.0.0_aaaa");
  for (const [path, content] of [
    ["dcp/dcp", "dcp binary"],
    ["managed/aspire-managed", "managed binary"],
    ["dashboard/Aspire.Dashboard", "dashboard binary"],
  ]) {
    mkdirSync(join(versionDir, path, ".."), { recursive: true });
    writeFileSync(join(versionDir, path), content);
  }
  chmodSync(join(versionDir, "dcp/dcp"), 0o755);
  const bundle = join(root, "home", "bundle");
  symlinkSync(versionDir, bundle);
  const props = join(root, "Directory.Packages.props");
  writeFileSync(props, '<Project><ItemGroup><PackageVersion Include="Aspire.Hosting.AppHost" Version="17.0.0" /></ItemGroup></Project>\n');
  const fingerprintFile = join(root, "aspire-bundle.fingerprint");
  const env = { ...process.env, PACKAGES_PROPS: props, FINGERPRINT_FILE: fingerprintFile };
  // A working directory with one regular file, so a walk that escapes into it
  // would succeed and print a fingerprint.
  const cwd = join(root, "cwd");
  mkdirSync(cwd);
  writeFileSync(join(cwd, "plain"), "plain");
  const run = (...args) => spawnSync("bash", [SCRIPT, ...args], { encoding: "utf8", env, cwd });
  const recorded = run("--print", bundle).stdout.trim();
  assert.match(recorded, /^[0-9a-f]{64}$/);
  const commit = (version) => writeFileSync(fingerprintFile, `# comment\nversion ${version}\n${PLATFORM} ${recorded}\n`);
  commit("17.0.0");
  return { root, versionDir, bundle, recorded, commit, run, fingerprintFile, check: () => run(bundle) };
}

test("a bundle that matches the committed fingerprint passes", (t) => {
  const f = fixture(t);
  const result = f.check();
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), f.recorded);
});

test("one changed byte fails", (t) => {
  const f = fixture(t);
  writeFileSync(join(f.versionDir, "dcp/dcp"), "dcp binarY");
  assert.equal(f.check().status, 1);
});

test("an added file fails", (t) => {
  const f = fixture(t);
  writeFileSync(join(f.versionDir, "dcp/extra.so"), "");
  assert.equal(f.check().status, 1);
});

test("a fingerprint committed for another Aspire version fails", (t) => {
  const f = fixture(t);
  f.commit("13.6.5");
  assert.equal(f.check().status, 1);
});

test("changed mtimes and a renamed version directory still pass", (t) => {
  const f = fixture(t);
  utimesSync(join(f.versionDir, "dcp/dcp"), new Date(0), new Date(0));
  const renamed = join(f.root, "home", "versions", "17.0.0_bbbb");
  renameSync(f.versionDir, renamed);
  rmSync(f.bundle);
  symlinkSync(renamed, f.bundle);
  const result = f.check();
  assert.equal(result.status, 0, result.stderr);
});

test("runtime lease files are ignored", (t) => {
  const f = fixture(t);
  mkdirSync(join(f.versionDir, ".leases"));
  writeFileSync(join(f.versionDir, ".leases", "12345"), "lease");
  const result = f.check();
  assert.equal(result.status, 0, result.stderr);
});

test("a missing bundle directory fails instead of hashing the working directory", (t) => {
  const f = fixture(t);
  const missing = join(f.root, "no-such-bundle");
  for (const result of [f.run("--print", missing), f.run(missing)]) {
    assert.notEqual(result.status, 0);
    assert.doesNotMatch(result.stdout, /[0-9a-f]{64}/);
  }
});

// --validate, the grammar the lockfix commit job and the check both apply.
const HASH = "a".repeat(64);
const VALID_FILE = `# comment\nversion 17.0.0\nlinux-x86_64 ${HASH}\nosx-arm64 ${"b".repeat(64)}\n`;

function validate(t, body, env = {}) {
  const root = mkdtempSync(join(tmpdir(), "aspire-validate-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const file = join(root, "aspire-bundle.fingerprint");
  writeFileSync(file, body);
  return spawnSync("bash", [SCRIPT, "--validate", file], { encoding: "utf8", env: { ...process.env, ...env } });
}

test("validate accepts comments, one version and distinct platform lines", (t) => {
  const result = validate(t, VALID_FILE);
  assert.equal(result.status, 0, result.stderr);
});

for (const [name, body] of [
  ["a version line only", "version 17.0.0\n"],
  ["two version lines", `version 17.0.0\nversion 17.1.0\nlinux-x86_64 ${HASH}\n`],
  ["no version line", `linux-x86_64 ${HASH}\n`],
  ["a platform listed twice", `version 17.0.0\nlinux-x86_64 ${HASH}\nlinux-x86_64 ${"b".repeat(64)}\n`],
  ["CRLF line ends", `version 17.0.0\r\nlinux-x86_64 ${HASH}\r\n`],
  ["a NUL byte", `#x\0version 17.1.0\nversion 17.0.0\nlinux-x86_64 ${HASH}\n`],
  ["a blank line", `version 17.0.0\n\nlinux-x86_64 ${HASH}\n`],
  ["a short hash", `version 17.0.0\nlinux-x86_64 ${"a".repeat(63)}\n`],
  ["another kind of line", `version 17.0.0\nlinux-x86_64 ${HASH}\nrun: curl evil\n`],
]) {
  test(`validate refuses ${name}`, (t) => {
    assert.equal(validate(t, body).status, 1);
  });
}

test("the committed fingerprint file follows the grammar", () => {
  const committed = fileURLToPath(new URL("../aspire-bundle.fingerprint", import.meta.url));
  const result = spawnSync("bash", [SCRIPT, "--validate", committed], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
});

test("the check fails when the committed file breaks the grammar, even with a matching value", (t) => {
  const f = fixture(t);
  writeFileSync(f.fingerprintFile, `# comment\n\nversion 17.0.0\n${PLATFORM} ${f.recorded}\n`);
  assert.equal(f.check().status, 1);
});

test("validate refuses when the parser itself fails", (t) => {
  const bin = mkdtempSync(join(tmpdir(), "aspire-validate-bin-"));
  t.after(() => rmSync(bin, { recursive: true, force: true }));
  writeFileSync(join(bin, "awk"), "#!/bin/sh\nexit 2\n", { mode: 0o755 });
  assert.equal(validate(t, VALID_FILE, { PATH: `${bin}:${process.env.PATH}` }).status, 1);
});

test("an added internal symlink fails", (t) => {
  const f = fixture(t);
  writeFileSync(join(f.root, "outside"), "outside bytes");
  symlinkSync(join(f.root, "outside"), join(f.versionDir, "dcp/extra"));
  assert.equal(f.check().status, 1);
});

test("a file swapped for a symlink fails, whatever its target holds", (t) => {
  const f = fixture(t);
  const outside = join(f.root, "outside");
  writeFileSync(outside, "managed binary");
  rmSync(join(f.versionDir, "managed/aspire-managed"));
  symlinkSync(outside, join(f.versionDir, "managed/aspire-managed"));
  assert.equal(f.check().status, 1);
  writeFileSync(outside, "other bytes");
  assert.equal(f.check().status, 1);
});

test("clearing an execute bit fails", (t) => {
  const f = fixture(t);
  chmodSync(join(f.versionDir, "dcp/dcp"), 0o644);
  assert.equal(f.check().status, 1);
});

// One file whose path ends in "dcp\n<sha> - ./managed/aspire-managed" prints a
// manifest line that reads as the two lines of the files it replaces.
test("a name that folds in a deleted file's line fails", (t) => {
  const f = fixture(t);
  const managedSha = createHash("sha256").update("managed binary").digest("hex");
  rmSync(join(f.versionDir, "dcp/dcp"));
  rmSync(join(f.versionDir, "managed/aspire-managed"));
  const forged = join(f.versionDir, "dcp", `dcp\n${managedSha} - .`, "managed");
  mkdirSync(forged, { recursive: true });
  writeFileSync(join(forged, "aspire-managed"), "dcp binary", { mode: 0o755 });
  assert.equal(f.check().status, 1);
});

// aspire-bundle-fetch.sh, the step before the check in every AppHost job. A
// stub `dnx` first on PATH logs its call and lays out a bundle at --install-path.
const FETCH = fileURLToPath(new URL("./aspire-bundle-fetch.sh", import.meta.url));

function fetchFixture(t) {
  const root = mkdtempSync(join(tmpdir(), "aspire-fetch-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const bin = join(root, "bin");
  mkdirSync(bin);
  const log = join(root, "dnx.log");
  writeFileSync(join(bin, "dnx"), [
    "#!/bin/sh",
    `echo "ASPIRE_HOME=$ASPIRE_HOME $*" >> "${log}"`,
    'while [ "$1" != --install-path ]; do shift; done',
    'mkdir -p "$2/versions/v1" && ln -sfn "$2/versions/v1" "$2/bundle"',
    "",
  ].join("\n"), { mode: 0o755 });
  const props = join(root, "Directory.Packages.props");
  writeFileSync(props, '<PackageVersion Include="Aspire.Hosting.AppHost" Version="17.0.0" />\n');
  const home = join(root, "home", ".aspire");
  const env = { ...process.env, PATH: `${bin}:${process.env.PATH}`, PACKAGES_PROPS: props };
  const fetch = () => spawnSync("bash", [FETCH, home], { encoding: "utf8", env });
  const calls = () => (existsSync(log) ? readFileSync(log, "utf8").trim().split("\n") : []);
  return { root, home, fetch, calls };
}

test("fetch sets up the bundle when the Aspire home is missing", (t) => {
  const f = fetchFixture(t);
  const result = f.fetch();
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(f.calls(), [`ASPIRE_HOME=${f.home} --yes aspire.cli@17.0.0 -- setup --install-path ${f.home}`]);
});

test("fetch sets up the bundle again when a restored link points at another home", (t) => {
  const f = fetchFixture(t);
  mkdirSync(join(f.home, "versions", "v1"), { recursive: true });
  symlinkSync("/home/other-runner/.aspire/versions/v1", join(f.home, "bundle"));
  const result = f.fetch();
  assert.equal(result.status, 0, result.stderr);
  assert.equal(f.calls().length, 1);
  assert.equal(existsSync(join(f.home, "bundle")), true);
});

test("fetch skips setup when the bundle link resolves", (t) => {
  const f = fetchFixture(t);
  mkdirSync(join(f.home, "versions", "v1"), { recursive: true });
  symlinkSync(join(f.home, "versions", "v1"), join(f.home, "bundle"));
  const result = f.fetch();
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(f.calls(), []);
});

// --emit in PR CI and --adopt in the lockfix compute job. The props name
// `propsVersion`; the committed file names `committedVersion`.
function pinFixture(t, { propsVersion, committedVersion }) {
  const root = mkdtempSync(join(tmpdir(), "aspire-pin-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const bundle = join(root, "bundle");
  mkdirSync(join(bundle, "dcp"), { recursive: true });
  writeFileSync(join(bundle, "dcp", "dcp"), "dcp", { mode: 0o755 });
  const props = join(root, "Directory.Packages.props");
  writeFileSync(props, `<PackageVersion Include="Aspire.Hosting.AppHost" Version="${propsVersion}" />\n`);
  const fingerprintFile = join(root, "aspire-bundle.fingerprint");
  const committed = `# header kept as is\nversion ${committedVersion}\n${PLATFORM} ${"0".repeat(64)}\n`;
  writeFileSync(fingerprintFile, committed);
  const env = { ...process.env, PACKAGES_PROPS: props, FINGERPRINT_FILE: fingerprintFile };
  const run = (...args) => spawnSync("bash", [SCRIPT, ...args], { encoding: "utf8", env });
  return { root, bundle, fingerprintFile, committed, run };
}

test("emit writes the committed format for the props' version, and the check accepts it", (t) => {
  const f = pinFixture(t, { propsVersion: "17.1.0", committedVersion: "17.0.0" });
  const out = join(f.root, "ci", "aspire-bundle.fingerprint");
  const result = f.run("--emit", f.bundle, out);
  assert.equal(result.status, 0, result.stderr);
  const value = f.run("--print", f.bundle).stdout.trim();
  assert.equal(readFileSync(out, "utf8"), `# header kept as is\nversion 17.1.0\n${PLATFORM} ${value}\n`);
  assert.equal(readFileSync(f.fingerprintFile, "utf8"), f.committed);
  writeFileSync(f.fingerprintFile, readFileSync(out));
  const check = f.run(f.bundle);
  assert.equal(check.status, 0, check.stderr);
});

function ciFile(f, version, hash = "c".repeat(64)) {
  const path = join(f.root, "ci", "aspire-bundle.fingerprint");
  mkdirSync(join(f.root, "ci"), { recursive: true });
  writeFileSync(path, `# header kept as is\nversion ${version}\n${PLATFORM} ${hash}\n`);
  return path;
}

test("adopt takes CI's file when it names the props' version and the committed file does not", (t) => {
  const f = pinFixture(t, { propsVersion: "17.1.0", committedVersion: "17.0.0" });
  const path = ciFile(f, "17.1.0");
  const result = f.run("--adopt", path);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(readFileSync(f.fingerprintFile, "utf8"), readFileSync(path, "utf8"));
});

const BUMP = { propsVersion: "17.1.0", committedVersion: "17.0.0" };
const SAME = { propsVersion: "17.0.0", committedVersion: "17.0.0" };
// An unchanged Aspire needs no new pin, so it stays quiet. A changed one whose
// pin did not arrive must still exit 0, so the healed locks are pushed, and
// leave a visible warning.
for (const [name, versions, ciVersion, hash, warns] of [
  ["Aspire is unchanged", SAME, "17.0.0", undefined, false],
  ["Aspire is unchanged and CI left no file", SAME, null, undefined, false],
  ["CI fingerprinted another version than the props", BUMP, "17.2.0", undefined, true],
  ["CI's file breaks the grammar", BUMP, "17.1.0", "short", true],
  ["CI left no file for a changed Aspire", BUMP, null, undefined, true],
]) {
  test(`adopt keeps the committed file when ${name}`, (t) => {
    const f = pinFixture(t, versions);
    const path = ciVersion ? ciFile(f, ciVersion, hash) : join(f.root, "ci", "missing");
    const result = f.run("--adopt", path);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(readFileSync(f.fingerprintFile, "utf8"), f.committed);
    assert.equal(/^::warning::/m.test(result.stdout), warns, result.stdout);
  });
}

test("changed reports whether the props' Aspire version differs from the committed one", (t) => {
  assert.equal(pinFixture(t, SAME).run("--changed").stdout, "changed=false\n");
  assert.equal(pinFixture(t, BUMP).run("--changed").stdout, "changed=true\n");
});

// The pin the lockfix commits must come from a fresh download that nothing in
// the PR's build could touch. These guards read the jobs with a strict parser
// for the block-YAML subset the workflows use: mappings, sequences of mappings,
// plain, '...' and "..." scalars, and |/> block scalars. Anything else throws,
// so an unknown construct fails the guard instead of slipping past it.
function yamlScalar(text) {
  const s = text.trim();
  const double = /^"((?:[^"\\]|\\.)*)"(?:\s+#.*)?$/.exec(s);
  if (double) return JSON.parse(`"${double[1]}"`);
  const single = /^'((?:[^']|'')*)'(?:\s+#.*)?$/.exec(s);
  if (single) return single[1].replaceAll("''", "'");
  if (/^["'[{&*!|>@`%]/.test(s)) throw new Error(`unsupported YAML scalar: ${s}`);
  return s.replace(/\s+#.*$/, "");
}

function parseYaml(source) {
  const lines = source.split("\n");
  let i = 0;
  const indentOf = (line) => line.length - line.trimStart().length;
  const skip = () => {
    while (i < lines.length && /^\s*(#.*)?$/.test(lines[i])) i++;
  };
  const node = (indent) => (lines[i].trimStart().startsWith("- ") ? sequence(indent) : mapping(indent));
  function sequence(indent) {
    const items = [];
    for (skip(); i < lines.length && indentOf(lines[i]) === indent && lines[i].trimStart().startsWith("- "); skip()) {
      lines[i] = " ".repeat(indent + 2) + lines[i].trimStart().slice(2);
      items.push(mapping(indent + 2));
    }
    return items;
  }
  function mapping(indent) {
    const map = {};
    for (skip(); i < lines.length && indentOf(lines[i]) === indent && !lines[i].trimStart().startsWith("- "); skip()) {
      const entry = /^("(?:[^"\\]|\\.)*"|'(?:[^']|'')*'|[^\s"'#-][^:]*?|-[^\s:][^:]*?):(?:\s+(.*))?$/.exec(lines[i].trimStart());
      if (!entry) throw new Error(`unsupported YAML line: ${lines[i]}`);
      const key = yamlScalar(entry[1]);
      const rest = entry[2] ?? "";
      if (Object.hasOwn(map, key)) throw new Error(`duplicate YAML key: ${key}`);
      i++;
      if (/^[|>][-+]?$/.test(rest)) {
        const body = [];
        while (i < lines.length && (/^\s*$/.test(lines[i]) || indentOf(lines[i]) > indent)) body.push(lines[i++]);
        while (body.length && /^\s*$/.test(body.at(-1))) body.pop();
        const pad = Math.min(...body.filter((line) => line.trim()).map(indentOf));
        const text = body.map((line) => line.slice(pad));
        map[key] = rest.startsWith("|") ? text.join("\n") : text.join(" ");
      } else if (rest === "" || rest.startsWith("#")) {
        skip();
        map[key] = i < lines.length && indentOf(lines[i]) > indent ? node(indentOf(lines[i])) : "";
      } else {
        map[key] = yamlScalar(rest);
      }
    }
    return map;
  }
  skip();
  const value = node(indentOf(lines[i]));
  skip();
  if (i !== lines.length) throw new Error(`unparsed YAML from: ${lines[i]}`);
  return value;
}

// One job of a workflow: the lines from its key under `jobs:` to the next job.
// The key must appear once at job indent, and the nearest earlier column-0 key
// must be `jobs:`, so text inside another key's block scalar cannot stand in for
// it. The block must be printable ASCII and LF only: a no-break space or a
// Unicode line break reads differently here than in GitHub's parser.
function jobFromText(text, name) {
  const lines = text.split("\n");
  const keyOf = (line) => (/^ {2}\S.*:\s*$/.test(line) && !/^\s*#/.test(line) ? yamlScalar(line.trim().slice(0, -1)) : null);
  const starts = lines.flatMap((line, index) => (keyOf(line) === name ? [index] : []));
  assert.equal(starts.length, 1, `expected one job ${name}, found ${starts.length}`);
  const [start] = starts;
  const parent = lines.slice(0, start).findLast((line) => /^\S/.test(line) && !line.startsWith("#"));
  assert.equal(parent?.trimEnd(), "jobs:", `job ${name} is not under jobs:`);
  let end = start + 1;
  while (end < lines.length && keyOf(lines[end]) === null) end++;
  const block = lines.slice(start, end).join("\n");
  assert.doesNotMatch(block, /[^\x20-\x7e\n]/, `job ${name} holds a character outside printable ASCII`);
  return parseYaml(block)[name];
}

const workflowJob = (file, name) => jobFromText(readFileSync(new URL(`../workflows/${file}`, import.meta.url), "utf8"), name);

const withoutNames = ({ name: _name, ...rest }) => rest;
const CHANGED = "steps.aspire.outputs.changed == 'true'";

test("the aspire-fingerprint job is exactly a fresh download, an emit and an upload", () => {
  const job = workflowJob("ci.yml", "aspire-fingerprint");
  assert.deepEqual(withoutNames({ ...job, steps: job.steps.map(withoutNames) }), {
    "runs-on": "ubuntu-26.04",
    "timeout-minutes": "15",
    if: "github.event_name == 'pull_request' && startsWith(github.head_ref, 'dependabot/nuget/')",
    permissions: { contents: "read" },
    steps: [
      { uses: "actions/checkout@v7", with: { "persist-credentials": "false" } },
      { id: "aspire", run: '.github/scripts/aspire-bundle-fingerprint.sh --changed >>"$GITHUB_OUTPUT"' },
      { if: CHANGED, uses: "actions/setup-dotnet@v6", with: { "dotnet-version": "10.0.x", cache: "false" } },
      {
        if: CHANGED,
        env: { HOME: "${{ runner.temp }}/home", NUGET_PACKAGES: "${{ runner.temp }}/nuget-packages" },
        run: 'mkdir -p "$HOME"\n.github/scripts/aspire-bundle-fetch.sh "$RUNNER_TEMP/aspire"',
      },
      {
        if: CHANGED,
        run: '.github/scripts/aspire-bundle-fingerprint.sh --emit "$RUNNER_TEMP/aspire/bundle" "$RUNNER_TEMP/aspire-fingerprint/aspire-bundle.fingerprint"',
      },
      {
        if: CHANGED,
        uses: "actions/upload-artifact@v7",
        with: {
          name: "aspire-bundle-fingerprint",
          path: "${{ runner.temp }}/aspire-fingerprint/aspire-bundle.fingerprint",
          "if-no-files-found": "error",
          "retention-days": "1",
        },
      },
    ],
  });
});

test("the lockfix downloads and adopts the artifact and file the producer uploads", () => {
  const upload = workflowJob("ci.yml", "aspire-fingerprint").steps.find((step) => step.uses?.startsWith("actions/upload-artifact@"));
  const compute = workflowJob("dependabot-lockfix.yml", "compute").steps;
  const download = compute.find((step) => step.uses?.startsWith("actions/download-artifact@"));
  const adopt = compute.find((step) => step.run?.includes("--adopt"));
  assert.equal(download.with.name, upload.with.name);
  const adopted = `${download.with.path.replace("${{ runner.temp }}", "$RUNNER_TEMP")}/${basename(upload.with.path)}`;
  assert.match(adopt.run, new RegExp(`--adopt "${adopted.replace(/[$.]/g, "\\$&")}"$`));
});

test("only the aspire-fingerprint job uploads the fingerprint artifact", () => {
  const ci = readFileSync(new URL("../workflows/ci.yml", import.meta.url), "utf8");
  const uploads = ci.match(/^\s*name:\s*["']?aspire-bundle-fingerprint["']?\s*$/gm) ?? [];
  assert.equal(uploads.length, 1);
});

test("the guard's YAML reader accepts quoting and refuses what it cannot read", () => {
  assert.deepEqual(parseYaml('"a-b":\n  c: \'it\'\'s\'\n  d: "\\u0063x" # note\n  e: |\n    one\n    two\n'), { "a-b": { c: "it's", d: "cx", e: "one\ntwo" } });
  assert.throws(() => parseYaml("a: {b: c}\n"));
  assert.throws(() => parseYaml("a: 1\na: 2\n"));
});

test("the job reader finds a quoted job key under jobs:", () => {
  assert.deepEqual(jobFromText('on: push\njobs:\n  "x":\n    runs-on: ubuntu\n', "x"), { "runs-on": "ubuntu" });
});

test("the job reader refuses a job copied into another key's block scalar", () => {
  const decoy = "run-name: |\n  x:\n    runs-on: decoy\n  y:\n";
  assert.throws(() => jobFromText(`${decoy}jobs:\n  x:\n    runs-on: ubuntu\n`, "x"), /expected one job/);
  assert.throws(() => jobFromText(`${decoy}jobs:\n  z:\n    runs-on: ubuntu\n`, "x"), /not under jobs:/);
});

for (const [name, text] of [
  ["a no-break space before #", "echo hi\u00a0#; printf probe"],
  ["a trailing no-break space", "echo hi\u00a0"],
  ["U+0085", "echo hi\u0085printf probe"],
  ["U+2028", "echo hi\u2028printf probe"],
  ["a carriage return", "echo hi\r"],
]) {
  test(`the job reader refuses ${name}`, () => {
    assert.throws(() => jobFromText(`jobs:\n  x:\n    steps:\n      - run: ${text}\n`, "x"), /outside printable ASCII/);
  });
}
