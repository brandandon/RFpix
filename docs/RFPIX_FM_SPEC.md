# RFpix FM: technical specification

**Version 1, for RFpix 0.7.1 and newer (November 2026).**

RFpix FM is the digital picture signal RFpix uses on FM radios, simplex or through a repeater. This
document describes it completely, so that anyone can recognize it on the air, see who is sending, and
decode what is sent, without RFpix. It is the public documentation that FCC Part 97 asks for a new
digital technique (§97.309(a)(4)). Anyone may use it, in any program, free.

Everything here is checked against RFpix's own code:

- **The tables** the signal is built from are in [rfpix-fm/](rfpix-fm/): the sync and pilot values, and
  the four error-correction codes.
- **A reference transmitter**, [rfpix-fm/rfpix_fm_encoder.py](rfpix-fm/rfpix_fm_encoder.py), written from
  this document alone (Python and numpy). Its sound matches RFpix's own to within rounding (1 part in 10
  million), and RFpix decodes the WAV files it makes.
- **Tests** in RFpix's test suite ([FmSpecTests.cs](../tests/RFpix.Tests/FmSpecTests.cs)) fail if the
  code and this document ever differ.
- **RFpix's own transmitter and receiver** are open source (GPL-3.0), in
  [src/RFpix/FmSignal](../src/RFpix/FmSignal).

Sections 4 to 8 are the definition of the signal. Section 9 (receiving) is guidance only.

## Contents

1. [In brief](#1-in-brief)
2. [Recognizing it on the air](#2-recognizing-it-on-the-air)
3. [Station identification and callsigns](#3-station-identification-and-callsigns)
4. [The sound: tones and symbols](#4-the-sound-tones-and-symbols)
5. [The frame: sync, header and pilots](#5-the-frame-sync-header-and-pilots)
6. [The speeds](#6-the-speeds)
7. [From bits to tones](#7-from-bits-to-tones)
8. [Files, pieces and frames](#8-files-pieces-and-frames)
9. [Receiving (guidance)](#9-receiving-guidance)
10. [Notes on the rules](#10-notes-on-the-rules)
11. [Check values](#11-check-values)
- [Appendix A: the seeded random numbers](#appendix-a-the-seeded-random-numbers)
- [Appendix B: the files](#appendix-b-the-files)

## 1. In brief

| | |
| --- | --- |
| Kind of signal | OFDM (many tones side by side), carried as audio through an FM or SSB voice transmitter |
| Audio band | 305 to 2977 Hz: about **2.7 kHz** wide |
| Tones | 115, one every 23.4375 Hz; 11 of them are known "pilot" tones |
| Symbol | 48 ms (42.7 ms plus a 5.3 ms guard): **20.8 symbols a second on each tone** |
| Modulation | QPSK, 16-QAM, 64-QAM or 256-QAM on each tone, depending on the speed |
| Error correction | LDPC, 2880-bit blocks, rate 1/2, 2/3 or 3/4, each block checked by a CRC-32 |
| Frame | 63 symbols, 3.024 s: 2 sync symbols, 1 header symbol, 60 data symbols |
| Speeds | FM 1 to FM 6 (RFpix offers FM 1 to FM 5): 1.8 to 11 kbit/s of file data |
| What it carries | Any file (usually a JPEG, JPEG 2000 or AVIF picture), cut into pieces with a fountain code; the sender's callsign and the file's name go in the signal too |
| Encryption | None. Every step is a published, fixed algorithm: nothing is secret, nothing depends on a key |
| Sound card rate | 48000 samples a second |

## 2. Recognizing it on the air

- **On a waterfall:** a flat-topped block of noise-like signal from about 300 Hz to 3 kHz, with no gaps,
  lasting the whole transmission (usually 10 seconds to 2 minutes).
- **By ear:** a steady hiss, like other OFDM modes (HamDRM, VARA).
- **To tell it from HamDRM:** HamDRM's tones are about 42 Hz or more apart and its symbols are about
  27 ms or shorter; RFpix FM's tones are 23.4375 Hz apart and its symbols are 48 ms.
- **For certain:** a receiver finds each frame by its two identical sync symbols (section 5), then
  reads the header, which says the speed. RFpix does this automatically, beside HamDRM.

A transmission is one frame after another, with nothing between them. A transmitter may key up a
moment before the first frame (RFpix waits 0.3 to 1.5 seconds, for a repeater to open).

## 3. Station identification and callsigns

The sender's callsign travels inside the signal, in a "details" block (section 8.3) in the first frame,
every fourth frame, and the last frame. RFpix shows it on the Receive tab and in the Gallery. A missing-
piece request (section 8.4) carries the asking station's callsign in every block.

That callsign is a convenience, **not the station's legal identification**. RFpix FM isn't one of the
digital codes §97.119(b) allows for identifying, so stations identify **by voice or CW**, as with
HamDRM.

## 4. The sound: tones and symbols

The sound is made at **48000 samples a second**. A transmitter at another rate must resample it.

**Tones.** Tone number *k* is at *k* × 23.4375 Hz (48000 / 2048). The signal uses tones *k* = 13 to
127 (304.7 to 2976.6 Hz), 115 in all.

- **Pilot tones:** *k* = 13, 25, 37, 49, 61, 73, 85, 97, 109, 121 and 127 (every 12th from 13, and
  the last).
- **Data tones:** the other 104. "Data tone *t*" means the *t*-th of them counting up from the
  lowest (*t* = 0 is *k* = 14, *t* = 103 is *k* = 126).
- **Edge tones:** *k* < 26 or *k* > 106 (below 609 Hz or above 2484 Hz). FM radios' audio is weakest
  there, so some speeds carry fewer bits on these tones. Of the data tones, 29 are edge tones and 75
  are middle tones.

**Symbols.** Each symbol gives every tone *k* a complex value *X*<sub>k</sub> (its amplitude and
phase). The symbol's 2048 samples are

> *x*[*n*] = (2 / 2048) × Re( Σ<sub>k=13..127</sub> *X*<sub>k</sub> e<sup>+j2πkn/2048</sup> ), for *n* = 0 to 2047

and it is sent as 2304 samples: its last 256 samples (the guard), then all 2048. That is, sample *i*
(0 to 2303) of the symbol is *x*[(*i* − 256) mod 2048]. Symbols follow one another with no gap, 48 ms
each.

**Level (not part of the definition).** RFpix scales each whole transmission so its RMS level is 0.175
of full scale, and clips the rare peaks beyond full scale (about 1 sample in 10000). Other
transmitters may set the level as they like.

## 5. The frame: sync, header and pilots

A frame is **63 symbols** (145152 samples, 3.024 s). Its symbols are numbered *s* = 0 to 62:

| Symbols | What they carry |
| --- | --- |
| *s* = 0, 1 | **Sync**: both the same, a known value on every tone |
| *s* = 2 | **Header**: the speed and the frame number, with the pilots |
| *s* = 3 to 62 | **Data**: 60 symbols, with the pilots |

**The known values** are all of the form e<sup>j(π/4 + *q*π/2)</sup>, that is (±1 ± j)/√2, for a
"quadrant" *q* = 0, 1, 2 or 3 (45°, 135°, 225°, 315°). The table
[rfpix-fm/sync-and-pilots.txt](rfpix-fm/sync-and-pilots.txt) gives, for every tone *k* from 13 to
127, its sync quadrant and its pilot-base quadrant. (RFpix makes them from .NET's seeded random numbers,
Appendix A: seed 9001 for the sync and 9002 for the pilots, one `Next(4)` per tone from *k* = 13 up.
The table is the definition.)

**Sync symbols** (*s* = 0 and 1): every tone 13 to 127, pilots included, carries its sync value.

**Pilots** (symbols *s* = 2 to 62): pilot tone *k* carries sign(*s*) × its pilot-base value, where
sign(*s*) is −1 if bit (*s* mod 32) of the number 0x5A3C96E1 is 1, and +1 if it is 0 (bit 0 is the
lowest).

**The header** (*s* = 2) carries 12 bits: the speed's number (section 6) in the top 4, and the
frame number (0, 1, 2 … within the transmission, going from 255 back to 0) in the bottom 8:

> header = speed × 256 + (frame number mod 256)

Data tone *t* (*t* = 0 to 95) carries header bit (11 − *t* mod 12), so the 12 bits go out highest first,
8 times over, across the band. Data tones 96 to 103 carry a 0 bit. Each bit is sent as **+1 for a 0, −1
for a 1** (BPSK). The pilot tones carry their pilots.

**Data symbols** (*s* = 3 to 62): the pilot tones carry their pilots, and the data tones carry the
frame's data (section 7).

## 6. The speeds

| Speed | Number in the header | Middle tones | Edge tones | Error correction | Data bits a block (*K*) | Bits a data symbol | Blocks a frame | Piece size | File data |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| FM 1 | 0 | QPSK (2 bits) | QPSK (2) | 1/2 | 1440 | 208 | 4 | 170 bytes | 1.8 kbit/s |
| FM 2 | 1 | 16-QAM (4) | QPSK (2) | 1/2 | 1440 | 358 | 7 | 170 bytes | 3.1 kbit/s |
| FM 3 | 2 | 16-QAM (4) | 16-QAM (4) | 2/3 | 1920 | 416 | 8 | 230 bytes | 4.9 kbit/s |
| FM 4 | 3 | 64-QAM (6) | 16-QAM (4) | 2/3 | 1920 | 566 | 11 | 230 bytes | 6.7 kbit/s |
| FM 5 | 4 | 64-QAM (6) | 64-QAM (6) | 3/4 | 2160 | 624 | 13 | 260 bytes | 8.9 kbit/s |
| FM 6 | 5 | 256-QAM (8) | 64-QAM (6) | 3/4 | 2160 | 774 | 16 | 260 bytes | 11.0 kbit/s |

- Bits a data symbol = 75 × (middle bits) + 29 × (edge bits).
- Blocks a frame = ⌊60 × (bits a data symbol) / 2880⌋.
- Piece size = *K* / 8 − 10 bytes (section 7.1).
- File data = blocks a frame × piece size × 8 / 3.024 s (before the details blocks and the fountain
  code's extra pieces).
- Header numbers 6 to 15 are spare: receivers ignore frames with them.
- RFpix's Send tab offers FM 1 to FM 5; RFpix receives all six.
- The rate 5/6 code in the files is not used by any speed yet.

## 7. From bits to tones

Each frame carries a fixed number of blocks (section 6). Each block carries one piece of a file (or the
file's details, or a request), goes through the error correction, and is spread over the whole frame.

### 7.1 A block's data

Each block's data is *K* / 8 bytes:

| Bytes | What |
| --- | --- |
| 0-1 | File number, 1 to 65535, chosen by the sender to tell its files apart (big-endian) |
| 2-3 | Pieces that rebuild the file, *k* (section 8.1) (big-endian) |
| 4-5 | Piece number: 0 and up for the file's pieces; 0xFFFF for the details (8.3); 0xFFFE for a request (8.4) (big-endian) |
| 6 to end − 5 | The piece: *K* / 8 − 10 bytes |
| last 4 | CRC-32 of all the bytes before it (big-endian) |

The CRC-32 is the usual one (as in zip files and Ethernet): reflected polynomial 0xEDB88320, starting
value 0xFFFFFFFF, and inverted at the end. The bytes become *K* bits, highest bit of byte 0 first.

### 7.2 Error correction (LDPC)

Each block of *K* data bits becomes *N* = 2880 bits: the *K* data bits as they are, then *M* = 2880 − *K*
parity bits *p*<sub>0</sub> … *p*<sub>M−1</sub>:

> *p*<sub>m</sub> = *p*<sub>m−1</sub> ⊕ (the XOR of the data bits listed for check *m*), with *p*<sub>−1</sub> = 0

The lists are in the files [rfpix-fm/ldpc-rate-1-2.txt](rfpix-fm/ldpc-rate-1-2.txt),
[2-3](rfpix-fm/ldpc-rate-2-3.txt), [3-4](rfpix-fm/ldpc-rate-3-4.txt) and
[5-6](rfpix-fm/ldpc-rate-5-6.txt): line *m* (counting from 0, after the comment lines) lists the data
bits (0 to *K* − 1) in check *m*. Every data bit is in 3 checks. In matrix terms, check *m* is the sum of
its data bits, parity bit *m* − 1 (when *m* > 0) and parity bit *m*, and every check sums to 0: an
irregular repeat-accumulate (IRA) code.

### 7.3 Spreading the blocks over the frame

A frame of speed number *v* holds *B* = 60 × (bits a data symbol) bits. They are spread with a fixed
shuffle of the numbers 0 to *B* − 1, the same for every frame of that speed:

```
positions = [0, 1, …, B − 1]
random = .NET Random(9100 + v)               (Appendix A)
for i = B − 1 down to 1:
    j = random.Next(i + 1)
    swap positions[i] and positions[j]
```

Start with *B* zero bits. Bit *j* of block *b* (*j* = 0 to 2879, blocks counting from 0) is put at
position positions[*b* × 2880 + *j*]. The positions no block reaches (*B* is a little more than
blocks × 2880) stay 0.

### 7.4 Scrambling

The *B* bits are then XORed, bit *i* with scrambling bit *i*, from this generator (32-bit unsigned
arithmetic, the same for every frame):

```
state = 0xACE1
for each bit i:
    state ^= state << 13
    state ^= state >> 17
    state ^= state << 5
    scrambling bit i = state & 1
```

### 7.5 Onto the tones

The scrambled bits are taken in order: data symbol 0 to 59, and in each, data tone *t* = 0 to 103, each
tone taking as many bits as its QAM carries (*n* = 2, 4, 6 or 8, from the speed's middle or edge
column). Each tone's *n* bits become a square QAM point:

- The first *n*/2 bits (first bit highest) are a Gray-coded number *g* for the real part; the last
  *n*/2 bits likewise for the imaginary part.
- Undo the Gray code: index = *g* ⊕ (*g* >> 1) ⊕ (*g* >> 2) ⊕ …
- Level = 2 × index − (*L* − 1), where *L* = 2<sup>n/2</sup> levels.
- Scale both parts by 1 / √(2(*L*² − 1)/3), so the average power is 1.

For example, with QPSK (*n* = 2), bits 00 give (−1 − j)/√2 and 11 give (+1 + j)/√2. With 16-QAM, bits
0010 give (−3 + 3j)/√10 (*g* = 0 for the real part: index 0, level −3; *g* = 2 for the imaginary: index
3, level +3).

## 8. Files, pieces and frames

### 8.1 The file and its pieces (the fountain code)

RFpix's fountain code turns a file into *k* source pieces and as many repair pieces as wanted; **any *k*
of them** (in practice almost always) rebuild the file. It is the same code RFpix uses with HamDRM's
"Fountain" repair data.

**Source pieces.** With piece size *P* (section 6), make a stream of 8 header bytes then the file:

| Bytes | What |
| --- | --- |
| 0 | 1 (the format) |
| 1-3 | The file's length in bytes (little-endian, at most 16777215) |
| 4-7 | The file's extension, lower case, without the dot, in Latin-1, padded with zeros (e.g. `jpg`, `jp2`, `avif`) |
| 8 on | The file |

*k* = ⌈(8 + length) / *P*⌉ (at most 16384). Pad the stream with zeros to *k* × *P* bytes. Source piece
*i* (piece number *i*, 0 to *k* − 1) is bytes *iP* to (*i* + 1)*P* − 1.

**Repair pieces.** Repair piece *r* (piece number *k* + *r*, *r* = 0, 1, 2 …) is, byte by byte,

> repair<sub>r</sub> = c<sub>r,0</sub> · source<sub>0</sub> + c<sub>r,1</sub> · source<sub>1</sub> + … + c<sub>r,k−1</sub> · source<sub>k−1</sub>

in GF(256) (bytes, adding by XOR, multiplying modulo the polynomial *x*<sup>8</sup> + *x*<sup>4</sup> +
*x*<sup>3</sup> + *x*<sup>2</sup> + 1, 0x11D). The *k* mixing numbers c<sub>r,0</sub> … c<sub>r,k−1</sub>
come from SplitMix64 (64-bit unsigned arithmetic, wrapping):

```
state = 0x5246706978466173 XOR (r << 32) XOR k
repeat until there are k numbers:
    state = state + 0x9E3779B97F4A7C15
    z = state
    z = (z XOR (z >> 30)) × 0xBF58476D1CE4E5B9
    z = (z XOR (z >> 27)) × 0x94D049BB133111EB
    z = z XOR (z >> 31)
    byte = z >> 56                     (the top 8 bits)
    if byte ≠ 0: the next number is byte
```

**Rebuilding.** Each piece received is a known combination of the source pieces (a source piece is
itself, a repair piece uses its mixing numbers). Once *k* independent ones have arrived, solving the
equations in GF(256) (Gaussian elimination) gives the source pieces; strip the 8-byte header and the
padding to get the file and its extension.

### 8.2 Filling the frames

A sender chooses how much repair to add, as a percentage: *R* = ⌈*k* × percent / 100⌉ repair pieces
(RFpix offers 20%, 50% and 75%). With *b* blocks a frame, the transmission has *F* frames, the smallest
number with at least ⌈(*k* + *R*) / *b*⌉ frames and room for all *k* + *R* pieces beside the details
blocks:

- **Frames with the details:** frame *f* (counting from 0) carries a details block (8.3) when *f* mod
  4 = 0 or *f* = *F* − 1 (the first, every fourth, and the last).
- **Room for pieces** = *F* × *b* − (the number of frames with details).
- If there is more room than pieces, the rest of the room is filled with more repair pieces (numbered
  on), which cost no extra time.

Each frame's blocks, in order: the details block first, if the frame has one, then the next pieces in
number order, from piece 0. (So block 0 of frame 0 is the details, then pieces 0, 1, 2 …)

A transmission answering a request (8.4) sends new repair pieces only, numbered on from the last ones
already sent, filling its frames in the same way.

### 8.3 The details block (piece number 0xFFFF)

Its piece holds the sender's callsign and the file's name:

| Bytes | What |
| --- | --- |
| 0 | 1 (the format) |
| 1 | Length of the callsign, *c* (at most 12) |
| 2 to 1 + *c* | The callsign, ASCII |
| 2 + *c* | Length of the name, *m* (at most 255, and cut to fit the piece) |
| 3 + *c* on | The name, UTF-8 (e.g. `tower.jpg`) |
| the rest | Zeros |

Its label (7.1) carries the file's number and *k*, like the file's pieces.

### 8.4 A request for missing pieces (piece number 0xFFFE)

A station that missed part of a file can ask the sender for more. The request is **one frame at FM 1**
(frame number 0, about 3 seconds), with the same block in all 4 of its blocks: the label holds the file
number and *k* of the file asked about, piece number 0xFFFE, and the piece:

| Bytes | What |
| --- | --- |
| 0 | 1 (the format) |
| 1-2 | The file's piece size *P* (big-endian), which tells its speed's piece size |
| 3-4 | How many more pieces the station needs (big-endian) |
| 5 | Length of the asking station's callsign, *c* (at most 12) |
| 6 to 5 + *c* | The callsign, ASCII |
| the rest | Zeros |

The sender answers, if it sent that file (same number, *k* and piece size), with new repair pieces at
the file's own speed. RFpix sends needed + 1 + ⌊needed / 5⌋ of them, so a lost one or two don't matter.
Any new repair pieces fill any station's gaps, so one answer serves every station that asked.

## 9. Receiving (guidance)

This section describes how RFpix receives the signal. It isn't part of the definition: any method that
gets the bits right will do.

1. **Remove a repeater's sub-audible tone** (CTCSS, 67-254 Hz), if any, with a notch filter.
2. **Find each frame** from its two identical sync symbols: correlate the sound with itself 2304
   samples later, over 2304 samples (a Schmidl-Cox detector). Where the match is above 0.3, pin the
   frame's exact start by correlating with the known sync sound, and keep it if that match is above
   0.25.
3. **Measure the channel** on each tone from the two sync symbols, then follow it through the frame
   with the pilots (fitting a line across the frame, weighting each pilot by its strength), and refine
   it with the decided data points.
4. **Read the header:** for each of the 12 bits, add up its 8 copies.
5. **Work out soft bits** (log-likelihood ratios) for every data tone from the QAM points, with the
   noise measured on each tone.
6. **Unscramble** (flip the sign of each soft bit whose scrambling bit is 1), **gather** each block's
   2880 soft bits from the spread positions, and **decode** the LDPC (RFpix uses layered normalized
   min-sum, scale 0.8, up to 60 rounds).
7. **Check the CRC-32.** Keep the blocks that pass; collect them by file (file number, *k* and piece
   size), and rebuild each file once it has *k* independent pieces.

Each frame stands alone, so a station that tunes in late, or loses the signal for a few seconds, picks
up at the next frame.

## 10. Notes on the rules

These notes are the RFpix project's reading of the US rules, not legal advice.

- **Public documentation.** Like PACTOR, VARA and HamDRM, RFpix FM relies on §97.309(a)(4): a
  technique whose technical characteristics are documented publicly. This document, its tables and the
  reference transmitter are that documentation, and RFpix's full source is public too.
- **Not secret.** The scrambling, spreading and fountain mixing are fixed, published arithmetic, there
  to make the signal robust; none of it hides anything. Anyone with RFpix (free), or this document, can
  read every transmission.
- **Bandwidth.** The signal occupies about 2.7 kHz (305 to 2977 Hz of audio). On HF data segments
  the limit has been 2.8 kHz since January 8, 2024 (it replaced the old 300-baud limit). On VHF and UHF,
  its symbol rate (20.8 symbols a second on each tone) is far inside the limits.
- **Identification.** By voice or CW (section 3).
- **What it's for.** RFpix FM was made for FM voice radios, simplex or through a repeater, where it is
  about twice as fast as HamDRM for the same signal. On fading HF paths, HamDRM is the better choice.

## 11. Check values

These let another implementation check itself step by step. The reference transmitter prints them with
`python3 rfpix_fm_encoder.py --vectors`, and RFpix's tests check them.

### 11.1 The pieces

| What | Value |
| --- | --- |
| .NET `Random(9102)`, the first five `Next(1000000)` | 841453, 513582, 956712, 315715, 629236 |
| Spreading for FM 3 (seed 9102, *B* = 24960): positions[0] to [7] | 24208, 16577, 12196, 2573, 17740, 8879, 21485, 19013 |
| Scrambling bits 0 to 31 | 11101110011011111011011100011111 |
| Pilot signs for *s* = 0 to 31 | − + + + + − − − + − − + − + + − + + − − − − + + + − + − − + − + |
| Sync quadrants, *k* = 13 to 16 | 0, 3, 1, 3 |
| Pilot-base quadrants, *k* = 13 to 16 | 2, 0, 0, 3 |
| CRC-32 of the ASCII bytes `123456789` | 0xCBF43926 |
| GF(256): 0x57 × 0x83 | 0x31 |
| Mixing numbers of repair piece *r* = 0 with *k* = 7 | 241, 204, 106, 42, 149, 217, 234 |

### 11.2 The speeds

The table in section 6 (bits a data symbol, blocks a frame, piece size) is itself a check.

### 11.3 A whole transmission

The file: 1500 bytes, byte *i* = (37*i* + 11) mod 256, extension `jpg`. Sent at **FM 3** with 20%
repair, file number 77, callsign `N0CALL`, name `test.jpg`:

- *k* = 7 source pieces, *R* = 2 repair pieces, *F* = 2 frames; frame 0 carries the details then pieces
  0 to 6, frame 1 the details then pieces 7 and 8 and 5 more repair pieces (9 to 13). 290304 samples in
  all.
- The first four samples, before the level is set: 0.003285, 0.003964, 0.005008, 0.006299.
- Frame 1's first data symbol (*s* = 3), tones 14, 15 and 16 (data tones 0 to 2): (1 + j)/√10,
  (3 + 3j)/√10, (−3 − 3j)/√10, that is 0.316228 + 0.316228j, 0.948683 + 0.948683j,
  −0.948683 − 0.948683j.

## Appendix A: the seeded random numbers

The sync and pilot tables and the spreading come from .NET's `System.Random` started from a fixed seed
(Knuth's subtractive generator, as .NET implements it for a given seed). The tables are published, so
only the spreading (7.3) needs it. In 32-bit signed integers, with MBIG = 2147483647 and MSEED =
161803398:

```
start(seed):                      (seeds here are small and positive)
    a = array of 56 integers, all 0
    mj = MSEED − seed
    a[55] = mj
    mk = 1
    ii = 0
    for i = 1 to 54:
        ii = (ii + 21) mod 55
        a[ii] = mk
        mk = mj − mk;  if mk < 0: mk += MBIG
        mj = a[ii]
    repeat 4 times:
        for i = 1 to 55:
            a[i] −= a[1 + (i + 30) mod 55];  if a[i] < 0: a[i] += MBIG
    inext = 0
    inextp = 21

sample():
    inext += 1;  if inext ≥ 56: inext = 1
    inextp += 1; if inextp ≥ 56: inextp = 1
    r = a[inext] − a[inextp]
    if r = MBIG: r −= 1
    if r < 0: r += MBIG
    a[inext] = r
    return r

Next(n):                          (a whole number from 0 to n − 1)
    return floor(sample() × (1.0 / MBIG) × n)      (in double-precision floating point)
```

The reference transmitter has the same in Python (class `NetRandom`), and the check values in 11.1 test
it.

## Appendix B: the files

| File | What |
| --- | --- |
| [rfpix-fm/sync-and-pilots.txt](rfpix-fm/sync-and-pilots.txt) | Each tone's sync and pilot-base quadrant (section 5) |
| [rfpix-fm/ldpc-rate-1-2.txt](rfpix-fm/ldpc-rate-1-2.txt), [2-3](rfpix-fm/ldpc-rate-2-3.txt), [3-4](rfpix-fm/ldpc-rate-3-4.txt), [5-6](rfpix-fm/ldpc-rate-5-6.txt) | The error-correction codes (section 7.2) |
| [rfpix-fm/rfpix_fm_encoder.py](rfpix-fm/rfpix_fm_encoder.py) | The reference transmitter: `python3 rfpix_fm_encoder.py picture.jpg picture.wav --speed 3 --call N0CALL` |

**Changes to this document.** Any change to the signal gets a new version of this document, and a new
header number for any new speed, so older receivers ignore what they can't read.
