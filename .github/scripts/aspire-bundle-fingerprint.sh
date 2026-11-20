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
#   aspire-bundle-fingerprint.sh <bundle-dir>           print and check
#   aspire-bundle-fingerprint.sh --print <bundle-dir>   print only
#   aspire-bundle-fingerprint.sh --refresh <work-dir>   when the Aspire version
#     differs from the committed one, set up that version's bundle under
#     <work-dir> and rewrite the fingerprint file (the Dependabot lockfix job)
#   aspire-bundle-fingerprint.sh --validate <file>      exit 0 only for a file in
#     the fingerprint grammar (the lockfix commit job runs this on its artifact)
#
# PACKAGES_PROPS and FINGERPRINT_FILE override the repository files (tests).
set -euo pipefail

# The grammar: "#" comment lines, exactly one "version <v>" line, and at least
# one "<os>-<arch> <sha256>" line, each key once. LF line ends only, no other
# control byte. A tool that fails makes the file invalid.
valid_file() {
  [[ -f "$1" ]] || return 1
  LC_ALL=C tr -d '\000-\011\013-\037\177' < "$1" | cmp -s - "$1" || return 1
  LC_ALL=C awk '
    /^#/ { next }
    /^version [0-9A-Za-z.+-]+$/ { versions++; next }
    /^[a-z0-9_]+-[a-z0-9_]+ [0-9a-f]+$/ && length($2) == 64 { if (keys[$1]++) bad = 1; platforms++; next }
    { bad = 1 }
    END { exit !(!bad && versions == 1 && platforms >= 1) }
  ' "$1" || return 1
}

mode=check
if [[ "${1:-}" == "--print" || "${1:-}" == "--refresh" || "${1:-}" == "--validate" ]]; then
  mode="${1#--}"
  shift
fi
target="${1:?usage: aspire-bundle-fingerprint.sh [--print|--refresh|--validate] <path>}"

if [[ "$mode" == validate ]]; then
  valid_file "$target" || {
    echo "::error::$target is not a valid Aspire bundle fingerprint file: '#' comments, one 'version <v>' line and one '<os>-<arch> <sha256>' line per platform, LF line ends only." >&2
    exit 1
  }
  exit 0
fi

scripts="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$scripts/../.." && pwd)"
props="${PACKAGES_PROPS:-$repo/Directory.Packages.props}"
fingerprint_file="${FINGERPRINT_FILE:-$repo/.github/aspire-bundle.fingerprint}"
version="$(sed -n 's/.*"Aspire\.Hosting\.AppHost" Version="\([^"]*\)".*/\1/p' "$props")"
platform="$(uname -s | tr '[:upper:]' '[:lower:]')-$(uname -m)"
committed() { awk -v key="$1" '$1 == key { print $2 }' "$fingerprint_file"; }

manifest() {
  # errexit is off inside the caller's command substitution, so stop here.
  cd -P "$1" || return 1
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

fingerprint() {
  (manifest "$1") | sha256sum | cut -d' ' -f1
}

if [[ "$mode" == refresh ]]; then
  if [[ "$(committed version)" == "$version" ]]; then
    echo "Aspire $version is already fingerprinted; nothing to refresh."
    exit 0
  fi
  # A temp HOME keeps the CLI's caches off the runner's own home.
  mkdir -p "$target/home"
  HOME="$target/home" "$scripts/aspire-bundle-fetch.sh" "$target/aspire"
  value="$(fingerprint "$target/aspire/bundle")"
  awk -v version="$version" -v key="$platform" -v value="$value" '
    $1 == "version" { print "version " version; next }
    $1 == key { print key " " value; seen = 1; next }
    { print }
    END { if (!seen) print key " " value }
  ' "$fingerprint_file" > "$fingerprint_file.new"
  mv "$fingerprint_file.new" "$fingerprint_file"
  echo "Fingerprinted Aspire $version on $platform: $value"
  exit 0
fi

actual="$(fingerprint "$target")"
echo "$actual"
if [[ "$mode" == print ]]; then
  exit 0
fi

expected_version="$(committed version)"
expected="$(committed "$platform")"
regenerate="d=\$(mktemp -d) && ASPIRE_HOME=\"\$d\" dnx --yes aspire.cli@$version -- setup --install-path \"\$d\" && .github/scripts/aspire-bundle-fingerprint.sh --print \"\$d/bundle\""

if [[ "$expected_version" != "$version" ]]; then
  echo "::error::.github/aspire-bundle.fingerprint is for Aspire '$expected_version', but Directory.Packages.props has Aspire.Hosting.AppHost '$version'. Regenerate the value on $platform with a portable .NET SDK, then set 'version $version' and '$platform <value>' in that file: $regenerate" >&2
  exit 1
fi
if [[ "$actual" != "$expected" ]]; then
  echo "::error::The Aspire CLI bundle at $target has fingerprint $actual, but .github/aspire-bundle.fingerprint expects '$expected' for Aspire $version on $platform. The bundle is not the one committed. If the change is expected, regenerate the value: $regenerate" >&2
  exit 1
fi
