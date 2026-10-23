#!/bin/bash
# package.sh - builds the Mac installer for one kind of Mac, on a Mac of that kind
# (GitHub's build does both: .github/workflows/build.yml).
#
#   tools/mac/package.sh x64     ->  dist/RFpix-<version>-mac-intel.pkg
#   tools/mac/package.sh arm64   ->  dist/RFpix-<version>-mac-apple-silicon.pkg
#
# What it does:
#   1. publishes RFpix for that kind of Mac, with .NET included (nothing else to install);
#   2. builds Hamlib, the radio-control library, from its official source (checked against its
#      SHA-256 fingerprint), and puts it next to RFpix;
#   3. makes RFpix.app: the program, its icon, and Info.plist (its name, version, and the
#      sentence macOS shows when asking to use the radio's sound input);
#   4. signs it "ad hoc" (Apple Silicon Macs only run signed programs; ad hoc isn't Apple's
#      Developer ID, so macOS warns the first time: see docs/TESTING.md);
#   5. wraps it in an installer (.pkg) that puts RFpix.app in Applications. Opening a newer
#      installer replaces the old RFpix; settings and pictures stay (they're kept elsewhere).
#
# Needs: the .NET 10 SDK and Xcode's command-line tools (xcode-select --install).

set -euo pipefail

ARCH="${1:-}"
case "$ARCH" in
  x64)   KIND=intel;         CLANG_ARCH=x86_64; HOST_ARCHS="x86_64,arm64" ;;
  arm64) KIND=apple-silicon; CLANG_ARCH=arm64;  HOST_ARCHS="arm64" ;;
  *) echo "Usage: $0 x64|arm64" >&2; exit 1 ;;
esac

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
RID="osx-$ARCH"
WORK="$ROOT/build/mac-$ARCH"
DIST="$ROOT/dist"
MIN_MACOS=14.0   # the oldest macOS .NET 10 runs on (Sonoma)

# The version, from RFpix.csproj: "0.5.2-beta" (shown), "0.5.2" (macOS wants numbers only).
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/RFpix/RFpix.csproj | head -1)"
NUMERIC="${VERSION%%-*}"
echo "RFpix $VERSION for $KIND Macs"

rm -rf "$WORK"
mkdir -p "$WORK" "$DIST"

# ---- 1. RFpix itself ----
dotnet publish src/RFpix/RFpix.csproj -c Release -r "$RID" --self-contained true -o "$WORK/publish"

# ---- 2. Hamlib ----
HAMLIB_VERSION=4.7.2
HAMLIB_SHA256=ae1fcf2dbc80ea0786ea8f047b09399c3f7737d1930442f61a031708ed33e88f
HAMLIB_TAR="$ROOT/build/hamlib-$HAMLIB_VERSION.tar.gz"
if [ ! -f "$HAMLIB_TAR" ]; then
  curl -fsSL --retry 3 -o "$HAMLIB_TAR" \
    "https://github.com/Hamlib/Hamlib/releases/download/$HAMLIB_VERSION/hamlib-$HAMLIB_VERSION.tar.gz"
fi
echo "$HAMLIB_SHA256  $HAMLIB_TAR" | shasum -a 256 -c -
tar xzf "$HAMLIB_TAR" -C "$WORK"
(
  cd "$WORK/hamlib-$HAMLIB_VERSION"
  # Only what RFpix needs: the library, with nothing from Homebrew (so it runs on any Mac).
  export CC="clang -arch $CLANG_ARCH" CFLAGS="-O2 -mmacosx-version-min=$MIN_MACOS" LDFLAGS="-mmacosx-version-min=$MIN_MACOS"
  export PKG_CONFIG_PATH="" PKG_CONFIG_LIBDIR=""
  ./configure --prefix="$WORK/hamlib" --disable-static --enable-shared \
    --without-libusb --without-readline --without-indi --without-cxx-binding --without-xml-support \
    --disable-html-matrix --disable-winradio --disable-parallel --disable-dependency-tracking --enable-silent-rules
  make -j"$(sysctl -n hw.ncpu)"
  make install
)
HAMLIB_LIB="$WORK/hamlib/lib/libhamlib.4.dylib"
cp "$HAMLIB_LIB" "$WORK/publish/libhamlib.4.dylib"
install_name_tool -id "@loader_path/libhamlib.4.dylib" "$WORK/publish/libhamlib.4.dylib"
cp "$WORK/hamlib-$HAMLIB_VERSION/COPYING.LIB" "$WORK/publish/hamlib-licence.txt"
# It must use only macOS's own libraries.
if otool -L "$WORK/publish/libhamlib.4.dylib" | tail -n +2 | grep -v -E '^\s+(/usr/lib/|/System/|@loader_path/libhamlib)'; then
  echo "Hamlib depends on a library that isn't part of macOS (above)." >&2; exit 1
fi
lipo -info "$WORK/publish/libhamlib.4.dylib"

# ---- 3. RFpix.app ----
APP="$WORK/root/RFpix.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$WORK/publish/." "$APP/Contents/MacOS/"
rm -f "$APP/Contents/MacOS/"*.pdb

ICONSET="$WORK/RFpix.iconset"
mkdir -p "$ICONSET"
ICON_SRC=src/RFpix/Assets/RFpix-icon-512.png
for size in 16 32 128 256 512; do
  sips -z $size $size "$ICON_SRC" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double "$ICON_SRC" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/RFpix.icns"

sed -e "s/@VERSION@/$VERSION/g" -e "s/@NUMERIC@/$NUMERIC/g" -e "s/@MIN_MACOS@/$MIN_MACOS/g" \
  tools/mac/Info.plist > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist"

# ---- 4. sign (ad hoc): each library first, then the app ----
find "$APP/Contents/MacOS" -name "*.dylib" -exec codesign --force --sign - {} \;
codesign --force --sign - --entitlements tools/mac/RFpix.entitlements "$APP/Contents/MacOS/RFpix"
codesign --force --sign - --entitlements tools/mac/RFpix.entitlements "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

# ---- 5. the installer ----
COMPONENTS="$WORK/components.plist"
pkgbuild --analyze --root "$WORK/root" "$COMPONENTS"
# Always into Applications (not wherever an older copy was moved to), replacing what's there.
plutil -replace 0.BundleIsRelocatable -bool NO "$COMPONENTS"
plutil -replace 0.BundleOverwriteAction -string upgrade "$COMPONENTS"
chmod +x tools/mac/scripts/preinstall
pkgbuild --root "$WORK/root" --component-plist "$COMPONENTS" --install-location /Applications \
  --scripts tools/mac/scripts --identifier io.github.brandandon.rfpix --version "$NUMERIC" \
  "$WORK/RFpix-component.pkg"

sed -e "s/@VERSION@/$VERSION/g" -e "s/@NUMERIC@/$NUMERIC/g" -e "s/@HOST_ARCHS@/$HOST_ARCHS/g" \
    -e "s/@MIN_MACOS@/$MIN_MACOS/g" -e "s/@KIND@/$( [ "$KIND" = intel ] && echo Intel || echo 'Apple Silicon' )/g" \
  tools/mac/distribution.xml > "$WORK/distribution.xml"
mkdir -p "$WORK/resources"
cp tools/mac/welcome.html "$WORK/resources/welcome.html"
sed -i '' -e "s/@VERSION@/${VERSION/-/ }/g" "$WORK/resources/welcome.html"
PKG="$DIST/RFpix-$VERSION-mac-$KIND.pkg"
productbuild --distribution "$WORK/distribution.xml" --package-path "$WORK" --resources "$WORK/resources" "$PKG"

echo "Made $PKG ($(du -h "$PKG" | cut -f1))"
