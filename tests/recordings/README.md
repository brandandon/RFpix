# Clean HamDRM test recordings

These are perfect, noise-free HamDRM transmissions used to test RFpix's decoder.
Each one carries a known image from the `images` folder, so a test can decode the
WAV and check that the result is byte-for-byte identical to the original.

They were made with QSSTV's own HamDRM transmitter (QSSTV commit `8c27d6d`, March 2026),
run headless by the small tool in `tools/qsstv-testgen`. Every file was then decoded
with QSSTV's own receiver and matched its source image exactly.

## Format

- WAV, 48 000 Hz, 16-bit, mono
- 1 second of silence before and after the signal
- Signal level −15 to −17 dBFS RMS, peaks −1 to −4 dBFS
- Station label (callsign) in the signal: `RFPIX`

## The files

| File | Image | Mode | Bandwidth | QAM | Interleave | Protection | Reed-Solomon | Length |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 01_modeA_2p5khz_qam64_long_high_rs0.wav | img01.jpg | A | 2.5 kHz | 64 | Long | High | None | 30 s |
| 02_modeA_2p5khz_qam16_short_low_rs0.wav | img02.jpg | A | 2.5 kHz | 16 | Short | Low | None | 31 s |
| 03_modeA_2p2khz_qam4_long_high_rs0.wav | img03.jpg | A | 2.2 kHz | 4 | Long | High | None | 52 s |
| 04_modeB_2p5khz_qam16_long_high_rs0.wav | img04.jpg | B | 2.5 kHz | 16 | Long | High | None | 42 s |
| 05_modeB_2p2khz_qam64_short_low_rs0.wav | img05.jpg | B | 2.2 kHz | 64 | Short | Low | None | 32 s |
| 06_modeB_2p5khz_qam4_long_high_rs0.wav | img06.jpg | B | 2.5 kHz | 4 | Long | High | None | 57 s |
| 07_modeE_2p5khz_qam4_long_high_rs0.wav | img07.jpg | E | 2.5 kHz | 4 | Long | High | None | 76 s |
| 08_modeE_2p2khz_qam16_short_low_rs0.wav | img08.jpg | E | 2.2 kHz | 16 | Short | Low | None | 47 s |
| 09_modeE_2p5khz_qam64_long_high_rs0.wav | img09.jpg | E | 2.5 kHz | 64 | Long | High | None | 40 s |
| 10_modeB_2p5khz_qam16_long_high_rs1.wav | img10.jpg | B | 2.5 kHz | 16 | Long | High | RS1 | 44 s |
| 11_modeB_2p5khz_qam16_long_high_rs2.wav | img11.jpg | B | 2.5 kHz | 16 | Long | High | RS2 | 48 s |
| 12_modeB_2p5khz_qam16_long_high_rs3.wav | img12.jpg | B | 2.5 kHz | 16 | Long | High | RS3 | 53 s |
| 13_modeB_2p5khz_qam16_long_high_rs4.wav | img13.jpg | B | 2.5 kHz | 16 | Long | High | RS4 | 63 s |
| 14_modeB_2p5khz_qam16_long_high_rs0_jp2.wav | img14.jp2 | B | 2.5 kHz | 16 | Long | High | None | 41 s |
| 15_modeA_2p5khz_qam64_long_high_rs0.wav | img15.jpg | A | 2.5 kHz | 64 | Long | High | None | 59 s |

Notes:

- Files 01–09 cover every mode, both bandwidths, all three QAM levels, both interleave
  settings and both protection levels.
- Files 10–13 are the same profile with each Reed-Solomon level. The receiver gets an
  `.rs1`–`.rs4` file and must decode it back to the original JPEG.
- File 14 sends a JPEG 2000 image, QSSTV's default picture format.
- File 15 is a larger 640×480 image (24 KB), for longer transmissions with many segments.
- File 04 (mode B, 2.5 kHz, 16-QAM, long, high) is the most common net profile and the
  best one to start with.

## Off-air recordings

Real recordings from the radio, with fading, noise and dropouts, will go in a separate
folder once we start toughening the decoder.
