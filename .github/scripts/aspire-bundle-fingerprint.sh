#!/usr/bin/env bash
# Checks the Aspire CLI bundle (DCP, dashboard) against the fingerprint committed
# in .github/aspire-bundle.fingerprint. Since Aspire 17 these binaries come from
# the CLI bundle, not from NuGet packages, so the lock files no longer pin them.
#
# The fingerprint is a SHA-256 over the sorted "sha256  ./relative/path" lines of
# every file in the bundle, so it ignores mtimes, ownership and the
# versions/<version>_<hash> directory name. .leases/ holds runtime lease files.
#
#   aspire-bundle-fingerprint.sh <bundle-dir>          print and check
#   aspire-bundle-fingerprint.sh --print <bundle-dir>  print only
#
# PACKAGES_PROPS and FINGERPRINT_FILE override the repository files (tests).
set -euo pipefail

print_only=false
if [[ "${1:-}" == "--print" ]]; then
  print_only=true
  shift
fi
bundle="${1:?usage: aspire-bundle-fingerprint.sh [--print] <bundle-dir>}"

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
props="${PACKAGES_PROPS:-$repo/Directory.Packages.props}"
fingerprint_file="${FINGERPRINT_FILE:-$repo/.github/aspire-bundle.fingerprint}"

actual="$(cd -P "$bundle" && find . -type f ! -path './.leases/*' -print0 \
  | LC_ALL=C sort -z | xargs -0 -r sha256sum | sha256sum | cut -d' ' -f1)"
echo "$actual"
if $print_only; then
  exit 0
fi

version="$(sed -n 's/.*"Aspire\.Hosting\.AppHost" Version="\([^"]*\)".*/\1/p' "$props")"
platform="$(uname -s | tr '[:upper:]' '[:lower:]')-$(uname -m)"
expected_version="$(awk '$1 == "version" { print $2 }' "$fingerprint_file")"
expected="$(awk -v key="$platform" '$1 == key { print $2 }' "$fingerprint_file")"
regenerate="d=\$(mktemp -d) && ASPIRE_HOME=\"\$d\" dnx --yes aspire.cli@$version -- setup --install-path \"\$d\" && .github/scripts/aspire-bundle-fingerprint.sh --print \"\$d/bundle\""

if [[ "$expected_version" != "$version" ]]; then
  echo "::error::.github/aspire-bundle.fingerprint is for Aspire '$expected_version', but Directory.Packages.props has Aspire.Hosting.AppHost '$version'. Regenerate the value on $platform with a portable .NET SDK, then set 'version $version' and '$platform <value>' in that file: $regenerate" >&2
  exit 1
fi
if [[ "$actual" != "$expected" ]]; then
  echo "::error::The Aspire CLI bundle at $bundle has fingerprint $actual, but .github/aspire-bundle.fingerprint expects '$expected' for Aspire $version on $platform. The bundle is not the one committed. If the change is expected, regenerate the value: $regenerate" >&2
  exit 1
fi
