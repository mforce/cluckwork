#!/usr/bin/env bash
# Sets up the Aspire CLI bundle for the Aspire.Hosting.AppHost version in
# Directory.Packages.props at <aspire-home>/bundle, the same setup the AppHost
# build runs when it finds none. It also runs when a bundle link restored from a
# cache points at another home, because such a link does not resolve.
#
#   aspire-bundle-fetch.sh <aspire-home>
#
# PACKAGES_PROPS overrides the repository file (tests).
set -euo pipefail

home="${1:?usage: aspire-bundle-fetch.sh <aspire-home>}"
if [[ -d "$home/bundle" ]]; then
  echo "Aspire CLI bundle found at $home/bundle."
  exit 0
fi

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
props="${PACKAGES_PROPS:-$repo/Directory.Packages.props}"
version="$(sed -n 's/.*"Aspire\.Hosting\.AppHost" Version="\([^"]*\)".*/\1/p' "$props")"
# setup validates the new layout through ASPIRE_HOME, so it must be the install path.
ASPIRE_HOME="$home" dnx --yes "aspire.cli@$version" -- setup --install-path "$home"
