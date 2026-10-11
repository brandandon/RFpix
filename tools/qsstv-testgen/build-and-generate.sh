#!/usr/bin/env bash
# Rebuilds the clean HamDRM test recordings in tests/recordings using QSSTV's own code.
# Runs on Ubuntu 24.04 (or WSL). Not part of the RFpix app; only needed to regenerate test files.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
WORK="${WORK:-$HOME/rfpix-testgen}"
QSSTV_COMMIT=8c27d6d169d8c6c197eb47c2089870e39bc06a02

${SUDO:-sudo} apt-get install -y qtbase5-dev qt5-qmake libqt5svg5-dev libqwt-qt5-dev libfftw3-dev \
  libhamlib-dev libasound2-dev libpulse-dev libv4l-dev libopenjp2-7-dev

mkdir -p "$WORK" && cd "$WORK"
[ -d qsstv ] || git clone https://github.com/ON4QZ/QSSTV.git qsstv
git -C qsstv checkout -q "$QSSTV_COMMIT"

# 1. Build QSSTV normally (this produces all the object files we reuse).
mkdir -p build && cd build
qmake ../qsstv/src/qsstv.pro
make -j"$(nproc)"

# 2. Build the two headless tools from QSSTV's objects plus our small replacements.
mk() { sed -n "s/^$1 *= *//p" Makefile | sed 's/\$(shell[^)]*)//g;s/\$(DEFINES)//g'; }
CXXFLAGS="$(mk CXXFLAGS)"; DEFINES="$(mk DEFINES)"; INCPATH="$(mk INCPATH)"; LIBS="$(mk LIBS)"; LFLAGS="$(mk LFLAGS)"
for s in drmwav drmwav_soundout drmrxwav dumpcells; do
  eval g++ -c $CXXFLAGS $DEFINES $INCPATH -I../qsstv/src -o "$s.tool.o" "$HERE/$s.cpp"
done
TX_OBJS=$(ls *.o | grep -v '\.tool\.o$' | grep -v -x -e main.o -e csoundout.o)
RX_OBJS=$(ls *.o | grep -v '\.tool\.o$' | grep -v -x -e main.o -e logbook.o)
eval g++ $LFLAGS -o drmwav drmwav.tool.o drmwav_soundout.tool.o $TX_OBJS $LIBS
eval g++ $LFLAGS -o drmrxwav drmrxwav.tool.o $RX_OBJS $LIBS
DUMP_OBJS=$(ls *.o | grep -v '\.tool\.o$' | grep -v -x -e main.o)
eval g++ $LFLAGS -o dumpcells dumpcells.tool.o $DUMP_OBJS $LIBS

# 3. Generate each recording, then decode it and check it matches the source image.
export QT_QPA_PLATFORM=offscreen
OUT="$REPO/tests/recordings"
while read -r n img m bw q il pr rs; do
  name=$(printf "%s_mode%s_%skhz_qam%s_%s_%s_rs%s" "$n" "$m" "${bw/./p}" "$q" "$il" "$pr" "$rs")
  [ "${img##*.}" = jp2 ] && name="${name}_jp2"
  ./drmwav "$OUT/images/$img" "$OUT/$name.wav" "$m" "$bw" "$q" "$il" "$pr" "$rs" RFPIX
  rm -rf "rx/$name"
  ./drmrxwav "$OUT/$name.wav" "rx/$name" > /dev/null
  cmp "$OUT/images/$img" "rx/$name/$img" && echo "  decoded OK"
done < "$HERE/manifest.txt"
