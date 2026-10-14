# Off-air recordings

Real HamDRM transmissions recorded with RFpix's Record button, kept so the decoder is
tested against real radio conditions as well as the clean recordings one folder up.

| File | What it is |
|---|---|
| `offair1_modeE_2p5khz_qam16_long_low_rs0_fm70cm.wav` | KD9DD, Oct 11, 2026: QSSTV on an IC-9700 (into a dummy load, minimum power) to an FT-991A, FM on 70 cm. Mode E, 2.5 kHz, 16-QAM, long interleave (QSSTV's menu said "short"; its labels are swapped), low protection, no Reed-Solomon. Signal quality about 14–15 dB. Trimmed to the transmission plus a few seconds each side. |
| `offair1_qsstv_decoded.jp2` | The picture as QSSTV's own receiver decoded it from that recording (QSSTV's trailing padding removed). |

Recording 1 taught RFpix to read a frame where it's due when the frame finder has a lapse:
before that, one skipped frame spoiled 5 packets and the picture fell 9 pieces short.
