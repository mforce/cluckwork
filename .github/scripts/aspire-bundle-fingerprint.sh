#!/usr/bin/env bash
# Checks the Aspire CLI bundle (DCP, dashboard) against the fingerprint committed
# in .github/aspire-bundle.fingerprint. Since Aspire 17 these binaries come from
# the CLI bundle, not from NuGet packages, so the lock files no longer pin them.
#
# The fingerprint is a SHA-256 over sorted "<sha256> <x|-> ./relative/path" lines,
# one per file outside the root .leases/ (runtime lease files), where x|- is the
# owner execute bit. It ignores mtimes, ownership, other mode bits and the
# versions/<version>_<hash> directory name. Any entry that is not a regular file
# or directory (a symlink, device, fifo or socket) fails the check, because the
# file hashes would not cover what it points at. So does a name with a control
# character, because a newline in one could stand in for another file's line.
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

manifest() {
  cd -P "$1"
  local other
  other="$(find . ! -path './.leases/*' ! -type f ! -type d -print -quit)"
  if [[ -n "$other" ]]; then
    echo "::error::The Aspire CLI bundle at $1 holds $other, which is not a regular file or directory." >&2
    return 1
  fi
  # A newline in a name could fake another file's manifest line.
  other="$(find . ! -path './.leases/*' -name '*[[:cntrl:]]*' -print -quit)"
  if [[ -n "$other" ]]; then
    echo "::error::The Aspire CLI bundle at $1 holds a name with a control character: ${other@Q}" >&2
    return 1
  fi
  find . -type f ! -path './.leases/*' -print0 | LC_ALL=C sort -z |
    while IFS= read -r -d '' file; do
      execute=-
      if (( 8#$(stat -c %a "$file") & 8#100 )); then execute=x; fi
      printf '%s %s %s\n' "$(sha256sum < "$file" | cut -d' ' -f1)" "$execute" "$file"
    done
}

actual="$( (manifest "$bundle") | sha256sum | cut -d' ' -f1)"
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
