#!/bin/bash
# package.sh - builds RFpix for Linux, on a Linux computer of that kind (GitHub's build does
# both: .github/workflows/build.yml).
#
#   tools/linux/package.sh x64     ->  dist/RFpix-<version>-linux-x64.AppImage    (64-bit PCs)
#                                      dist/rfpix_<version>_amd64.deb (0.6.0-beta: GitHub won't keep a "~" in a name)
#   tools/linux/package.sh arm64   ->  dist/RFpix-<version>-linux-arm64.AppImage  (Raspberry Pi 4/5, 64-bit)
#                                      dist/rfpix_<version>_arm64.deb
#
# What it does:
#   1. publishes RFpix for that kind of computer, with .NET included (nothing else to install);
#   2. builds Hamlib, the radio-control library, from its official source (checked against its
#      SHA-256 fingerprint), and puts it next to RFpix;
#   3. makes the AppImage: one file that runs on almost any Linux;
#   4. makes the .deb: installs in /opt/rfpix, with a menu entry, on Ubuntu, Mint, Debian and
#      Raspberry Pi OS. A newer .deb installs over an older one; settings and pictures stay.
#
# Needs: the .NET 10 SDK, a C compiler and make (build-essential), dpkg-deb, curl.
# (For trying the packaging without publishing again: RFPIX_PUBLISHED=<folder> uses that folder.)

set -euo pipefail

ARCH="${1:-}"
case "$ARCH" in
  x64)   DEB_ARCH=amd64; AI_ARCH=x86_64;  TOOL_SHA256=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0 ;;
  arm64) DEB_ARCH=arm64; AI_ARCH=aarch64; TOOL_SHA256=f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158 ;;
  *) echo "Usage: $0 x64|arm64" >&2; exit 1 ;;
esac

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
WORK="$ROOT/build/linux-$ARCH"
DIST="$ROOT/dist"

# The version, from RFpix.csproj: "0.6.0-beta"; Debian writes a test version with "~" (0.6.0~beta
# comes before 0.6.0).
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/RFpix/RFpix.csproj | head -1)"
DEB_VERSION="${VERSION//-/\~}"
echo "RFpix $VERSION for Linux ($ARCH)"

rm -rf "$WORK"
mkdir -p "$WORK" "$DIST"

# ---- 1. RFpix itself ----
PUBLISH="$WORK/publish"
if [ -n "${RFPIX_PUBLISHED:-}" ]; then
  cp -R "$RFPIX_PUBLISHED" "$PUBLISH"
else
  # InvariantGlobalization: no need for the ICU library, whose package name differs on every Linux.
  dotnet publish src/RFpix/RFpix.csproj -c Release -r "linux-$ARCH" --self-contained true \
    -p:InvariantGlobalization=true -o "$PUBLISH"
fi
rm -f "$PUBLISH"/*.pdb

# ---- 2. Hamlib ----
HAMLIB_VERSION=4.7.2
HAMLIB_SHA256=ae1fcf2dbc80ea0786ea8f047b09399c3f7737d1930442f61a031708ed33e88f
HAMLIB_TAR="$ROOT/build/hamlib-$HAMLIB_VERSION.tar.gz"
if [ ! -f "$HAMLIB_TAR" ]; then
  curl -fsSL --retry 3 -o "$HAMLIB_TAR" \
    "https://github.com/Hamlib/Hamlib/releases/download/$HAMLIB_VERSION/hamlib-$HAMLIB_VERSION.tar.gz"
fi
echo "$HAMLIB_SHA256  $HAMLIB_TAR" | sha256sum -c -
tar xzf "$HAMLIB_TAR" -C "$WORK"
(
  cd "$WORK/hamlib-$HAMLIB_VERSION"
  # Only what RFpix needs: the library, using nothing but the C library (so it runs on any Linux).
  CFLAGS="-O2" ./configure --prefix="$WORK/hamlib" --disable-static --enable-shared \
    --without-libusb --without-readline --without-indi --without-cxx-binding --without-xml-support \
    --disable-html-matrix --disable-winradio --disable-parallel --disable-dependency-tracking --enable-silent-rules
  make -j"$(nproc)"
  make install
)
cp -L "$WORK/hamlib/lib/libhamlib.so.4" "$PUBLISH/libhamlib.so.4"
strip --strip-unneeded "$PUBLISH/libhamlib.so.4"
cp "$WORK/hamlib-$HAMLIB_VERSION/COPYING.LIB" "$PUBLISH/hamlib-licence.txt"
NEEDED="$(readelf -d "$PUBLISH/libhamlib.so.4" | sed -n 's/.*(NEEDED).*\[\(.*\)\]/\1/p')"
echo "Hamlib needs: $(echo $NEEDED)"
for lib in $NEEDED; do
  case "$lib" in
    libc.so.*|libm.so.*|libpthread.so.*|libdl.so.*|librt.so.*|ld-linux*) ;;
    *) echo "Hamlib depends on $lib, which isn't part of every Linux." >&2; exit 1 ;;
  esac
done
chmod 755 "$PUBLISH/RFpix"

ICON=src/RFpix/Assets/RFpix-icon-512.png

desktop_file() {   # $1: the command to run
  cat <<DESKTOP
[Desktop Entry]
Type=Application
Name=RFpix
GenericName=SSTV pictures by radio
Comment=Send and receive digital (HamDRM) and analog SSTV pictures over ham radio
Exec=$1
Icon=rfpix
Terminal=false
Categories=Network;HamRadio;
Keywords=SSTV;HamDRM;EasyPal;QSSTV;MMSSTV;ham;amateur;radio;
StartupWMClass=RFpix
DESKTOP
}

# ---- 3. the AppImage ----
APPDIR="$WORK/RFpix.AppDir"
mkdir -p "$APPDIR/usr/bin"
cp -R "$PUBLISH/." "$APPDIR/usr/bin/"
cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
# Starts RFpix from inside the AppImage.
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/RFpix" "$@"
APPRUN
chmod 755 "$APPDIR/AppRun"
desktop_file RFpix > "$APPDIR/rfpix.desktop"
cp "$ICON" "$APPDIR/rfpix.png"
ln -sf rfpix.png "$APPDIR/.DirIcon"

TOOL="$ROOT/build/appimagetool-$AI_ARCH.AppImage"
if [ ! -f "$TOOL" ]; then
  curl -fsSL --retry 3 -o "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-$AI_ARCH.AppImage"
fi
echo "$TOOL_SHA256  $TOOL" | sha256sum -c -
chmod 755 "$TOOL"
APPIMAGE_OUT="$DIST/RFpix-$VERSION-linux-$ARCH.AppImage"
# (APPIMAGE_EXTRACT_AND_RUN: the tool runs without FUSE, which build machines often lack)
APPIMAGE_EXTRACT_AND_RUN=1 ARCH="$AI_ARCH" "$TOOL" --no-appstream "$APPDIR" "$APPIMAGE_OUT"
chmod 755 "$APPIMAGE_OUT"

# ---- 4. the .deb ----
DEB="$WORK/deb"
mkdir -p "$DEB/DEBIAN" "$DEB/opt/rfpix" "$DEB/usr/bin" "$DEB/usr/share/applications" \
         "$DEB/usr/share/icons/hicolor/512x512/apps" "$DEB/usr/share/doc/rfpix"
cp -R "$PUBLISH/." "$DEB/opt/rfpix/"
ln -s /opt/rfpix/RFpix "$DEB/usr/bin/rfpix"
desktop_file rfpix > "$DEB/usr/share/applications/rfpix.desktop"
cp "$ICON" "$DEB/usr/share/icons/hicolor/512x512/apps/rfpix.png"
cat > "$DEB/usr/share/doc/rfpix/copyright" <<COPYRIGHT
RFpix: https://github.com/brandandon/RFpix
Licence: GPL (see the LICENSE file in the source).
Includes Hamlib $HAMLIB_VERSION (LGPL, /opt/rfpix/hamlib-licence.txt), the .NET runtime (MIT),
Avalonia (MIT), Magick.NET and ImageMagick (Apache), CoreJ2K (BSD), FluentFTP and SSH.NET (MIT).
COPYRIGHT
INSTALLED_KB="$(du -sk "$DEB" | cut -f1)"
cat > "$DEB/DEBIAN/control" <<CONTROL
Package: rfpix
Version: $DEB_VERSION
Architecture: $DEB_ARCH
Section: hamradio
Priority: optional
Installed-Size: $INSTALLED_KB
Depends: libc6 (>= 2.35), libgcc-s1, libstdc++6, libpulse0, libx11-6, libice6, libsm6, libfontconfig1, libssl3 | libssl3t64
Maintainer: RFpix <https://github.com/brandandon/RFpix/issues>
Homepage: https://github.com/brandandon/RFpix
Description: Digital (HamDRM) and analog SSTV pictures over ham radio
 RFpix sends and receives pictures by radio: digital SSTV (HamDRM, as EasyPal
 and QSSTV) and analog SSTV (Martin, Scottie, Robot, PD, as MMSSTV). It fits
 pictures to the time you choose, repairs missing pieces (BSR), keeps a gallery,
 and can put pictures on your website. Radio control through Hamlib.
CONTROL
DEB_OUT="$DIST/rfpix_${VERSION}_${DEB_ARCH}.deb"
dpkg-deb --root-owner-group -Zxz --build "$DEB" "$DEB_OUT"

echo "Made $APPIMAGE_OUT ($(du -h "$APPIMAGE_OUT" | cut -f1))"
echo "Made $DEB_OUT ($(du -h "$DEB_OUT" | cut -f1))"
