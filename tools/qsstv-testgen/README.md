# qsstv-testgen

A small helper that makes the clean HamDRM test recordings in `tests/recordings`.
It is not part of the RFpix app, and you don't need it to build or run RFpix.

## What it does

It reuses QSSTV's own HamDRM code (GPL, by ON4QZ and PA0MBO, based on the Dream DRM
project) to make two command-line programs:

- **drmwav** feeds a file to QSSTV's HamDRM transmitter and writes the audio to a WAV
  file instead of a sound card. `drmwav_soundout.cpp` is the piece that swaps the sound
  card for a WAV file.
- **drmrxwav** feeds a WAV file to QSSTV's HamDRM receiver and saves whatever it decodes.
  It is used to prove each recording is valid.

Using QSSTV's code directly means the test signals are exactly what a real QSSTV station
sends, which is also what EasyPal stations understand.

## Files

- `drmwav.cpp`, `drmwav_soundout.cpp`: the transmit tool
- `drmrxwav.cpp`: the receive (checking) tool
- `dumpcells.cpp`: prints QSSTV's transmit cell layout (which cells are pilots, FAC or
  picture data, and each pilot's value) for every mode and bandwidth.
  `tests/reference/qsstv-cell-layouts.txt` was made from its output (one frame per
  layout, picture-data cells left out), and a test checks RFpix's layout against it
- `manifest.txt`: one line per recording: number, image, mode, bandwidth, QAM,
  interleave, protection, Reed-Solomon level
- `build-and-generate.sh`: installs the libraries, builds QSSTV at a fixed commit,
  builds both tools, then makes and checks every recording

## Regenerating the recordings

On Ubuntu 24.04 or in WSL:

```
tools/qsstv-testgen/build-and-generate.sh
```

To add a recording, add an image to `tests/recordings/images`, add a line to
`manifest.txt`, and run the script again. Output is repeatable: the same inputs
produce byte-identical WAV files.
