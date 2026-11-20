// Self-tests for the Aspire CLI bundle fingerprint check. Run with
// `node --test .github/scripts/aspire-bundle-fingerprint.test.mjs`.
//
// Each case builds a fake bundle home (versions/<id>/ plus a bundle symlink),
// records its fingerprint as the committed value, changes one thing, and
// asserts the check's exit status.

import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, renameSync, rmSync, symlinkSync, utimesSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
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
  const bundle = join(root, "home", "bundle");
  symlinkSync(versionDir, bundle);
  const props = join(root, "Directory.Packages.props");
  writeFileSync(props, '<Project><ItemGroup><PackageVersion Include="Aspire.Hosting.AppHost" Version="17.0.0" /></ItemGroup></Project>\n');
  const fingerprintFile = join(root, "aspire-bundle.fingerprint");
  const env = { ...process.env, PACKAGES_PROPS: props, FINGERPRINT_FILE: fingerprintFile };
  const run = (...args) => spawnSync("bash", [SCRIPT, ...args], { encoding: "utf8", env });
  const recorded = run("--print", bundle).stdout.trim();
  assert.match(recorded, /^[0-9a-f]{64}$/);
  const commit = (version) => writeFileSync(fingerprintFile, `# comment\nversion ${version}\n${PLATFORM} ${recorded}\n`);
  commit("17.0.0");
  return { root, versionDir, bundle, recorded, commit, check: () => run(bundle) };
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
