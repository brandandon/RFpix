#!/bin/bash
# build.sh - builds Hamlib, the radio-control library, from its official source, for the Mac and
# Linux packages (tools/mac/package.sh and tools/linux/package.sh call it).
#
#   tools/hamlib/build.sh <work folder>
#
# The version and the source's SHA-256 fingerprint come from tools/hamlib/Hamlib.props (written
# once there, for Windows too). The source is downloaded once into build/, checked against the
# fingerprint, then built in <work folder> with only what RFpix needs: the library, using nothing
# but the system's own C library, so it runs on any Mac or Linux. It's installed in
# <work folder>/hamlib, with its licence (COPYING.LIB) and its version (VERSION) beside it.
#
# The compiler settings come from the caller: the Mac sets CC, CFLAGS and LDFLAGS for the kind of
# Mac; Linux just CFLAGS.

set -euo pipefail

WORK="${1:?Usage: $0 <work folder>}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
PROPS="$HERE/Hamlib.props"
HAMLIB_VERSION="$(sed -n 's:.*<HamlibVersion>\(.*\)</HamlibVersion>.*:\1:p' "$PROPS")"
HAMLIB_SHA256="$(sed -n 's:.*<HamlibSourceSha256>\(.*\)</HamlibSourceSha256>.*:\1:p' "$PROPS")"
[ -n "$HAMLIB_VERSION" ] && [ -n "$HAMLIB_SHA256" ] || { echo "Couldn't read Hamlib's version from $PROPS." >&2; exit 1; }
echo "Hamlib $HAMLIB_VERSION"

mkdir -p "$ROOT/build" "$WORK"
TAR="$ROOT/build/hamlib-$HAMLIB_VERSION.tar.gz"
if [ ! -f "$TAR" ]; then
  curl -fsSL --retry 3 -o "$TAR" \
    "https://github.com/Hamlib/Hamlib/releases/download/$HAMLIB_VERSION/hamlib-$HAMLIB_VERSION.tar.gz"
fi
if command -v sha256sum > /dev/null; then echo "$HAMLIB_SHA256  $TAR" | sha256sum -c -
else echo "$HAMLIB_SHA256  $TAR" | shasum -a 256 -c -; fi

tar xzf "$TAR" -C "$WORK"
(
  cd "$WORK/hamlib-$HAMLIB_VERSION"
  ./configure --prefix="$WORK/hamlib" --disable-static --enable-shared \
    --without-libusb --without-readline --without-indi --without-cxx-binding --without-xml-support \
    --disable-html-matrix --disable-winradio --disable-parallel --disable-dependency-tracking --enable-silent-rules
  make -j"$(nproc 2> /dev/null || sysctl -n hw.ncpu)"
  make install
)
cp "$WORK/hamlib-$HAMLIB_VERSION/COPYING.LIB" "$WORK/hamlib/COPYING.LIB"
echo "$HAMLIB_VERSION" > "$WORK/hamlib/VERSION"
