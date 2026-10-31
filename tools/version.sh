#!/bin/bash
# version.sh - prints RFpix's version, as written in src/RFpix/RFpix.csproj (e.g. "0.6.0-beta"),
# for the Mac and Linux packaging scripts. (Windows' build reads the same line itself.)

set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/src/RFpix/RFpix.csproj" | head -1)"
[ -n "$VERSION" ] || { echo "Couldn't read the version from RFpix.csproj." >&2; exit 1; }
echo "$VERSION"
