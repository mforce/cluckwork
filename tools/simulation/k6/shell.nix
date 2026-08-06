# tools/simulation/k6/shell.nix — PR #279 review: pin k6 to a known nixpkgs
# revision instead of floating on whatever the host's `nixpkgs` channel/flake
# happens to resolve to right now.
#
# WHY THIS MATTERS: baseline.js's inter-phase drain gap (see that file's
# header, "WHY THE DRAIN GAP IS THE FIX") relies on empirically-probed k6 VU
# **pool-reuse** behavior — confirmed live, repeatedly, against exactly
# `k6 v2.0.0`. A silent k6 upgrade (a bare `nix-shell -p k6` tracks the
# channel, which moves over time) could change that scheduling behavior
# without anything in this harness noticing, quietly invalidating the
# zero-collision guarantee the per-user capacity coverage check
# (capacity_user_owner in baseline.js) depends on. Pinning the exact nixpkgs
# revision pins the exact k6 build.
#
# Run k6 through THIS shell, not a bare `nix-shell -p k6`:
#
#   nix-shell tools/simulation/k6/shell.nix --run 'k6 run tools/simulation/k6/baseline.js'
#
# run-baseline.sh does this automatically (see its K6_SHELL_NIX var) and
# additionally asserts `k6 version` against EXPECTED_K6_VERSION at run start,
# failing loudly on any mismatch — so a hand-edited pin here that drifts from
# that recorded expectation can't silently invalidate a run either.
#
# The pinned revision below is nixos-26.05's b3fe9581c9061c749abef42b6d4ee7b7c05c33fa
# (the exact revision this repo's dev box's own nixpkgs channel resolved to
# when this pin was created — see tools/simulation/README.md), which builds
# k6 v2.0.0. Bumping this pin (a deliberate, reviewed k6 upgrade) means
# re-verifying the drain-gap VU-scheduling behavior against the new version
# (file header above) and updating EXPECTED_K6_VERSION in run-baseline.sh to
# match, together, in the same change.
#
# STALE as of the v2.1.0 bump below: run-baseline.sh's EXPECTED_K6_VERSION is
# now v2.1.0 (re-verified live via a bare `k6` on PATH — see its own comment
# for the evidence), but THIS pin was not re-verified against v2.1.0 (no nix
# environment was available to build-and-confirm a candidate revision) and
# still resolves to v2.0.0. A nix-shell run will therefore correctly fail
# run-baseline.sh's preflight until someone with nix bumps this revision to
# one confirmed to build k6 v2.1.0. That failure is the guard doing its job,
# not a bug — do not silence it by loosening EXPECTED_K6_VERSION instead.
{
  pkgs ? import (fetchTarball {
    url = "https://github.com/NixOS/nixpkgs/archive/b3fe9581c9061c749abef42b6d4ee7b7c05c33fa.tar.gz";
    sha256 = "1ydrj921s009rzpsjg2vhq752l3r52g66iwr7pk1x00ldf5glpyr";
  }) { }
}:
pkgs.mkShell {
  buildInputs = [ pkgs.k6 ];
}
