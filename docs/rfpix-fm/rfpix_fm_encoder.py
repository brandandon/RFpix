#!/usr/bin/env python3
"""A reference transmitter for RFpix FM, written from docs/RFPIX_FM_SPEC.md alone.

It turns a file (usually a picture) into the sound RFpix sends, as a 48 kHz WAV file. RFpix's
own transmitter is C# (src/RFpix/FmSignal); this one is independent, to show that the specification
is complete. Its sound matches RFpix's own to within rounding (about 1 part in 10 million).

    python3 rfpix_fm_encoder.py picture.jpg picture.wav --speed 3 --call N0CALL
    python3 rfpix_fm_encoder.py --vectors          # print the check values in the spec, section 11

Needs Python 3.8 or later and numpy. The tables (sync-and-pilots.txt, ldpc-rate-*.txt) are read
from this script's own folder. Section numbers below are the spec's.
"""
import argparse, os, struct, sys, wave, zlib
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))

# ---- 4. The sound: tones and symbols ----
RATE, N, GUARD = 48000, 2048, 256
SYMBOL = N + GUARD
FIRST, LAST = 13, 127
CARRIERS = list(range(FIRST, LAST + 1))
PILOTS = [k for k in CARRIERS if (k - FIRST) % 12 == 0 or k == LAST]
DATA = [k for k in CARRIERS if k not in PILOTS]
SYNC_SYMBOLS, HEADER_SYMBOLS, DATA_SYMBOLS = 2, 1, 60
FRAME_SYMBOLS = SYNC_SYMBOLS + HEADER_SYMBOLS + DATA_SYMBOLS
LEVEL = 0.175          # RFpix's average level (not part of the signal's definition)


def is_edge(k):
    return k < 26 or k > 106


# ---- 6. The speeds: id -> (name, mid bits, edge bits, code rate index) ----
SPEEDS = {0: ("FM 1", 2, 2, 0), 1: ("FM 2", 4, 2, 0), 2: ("FM 3", 4, 4, 1),
          3: ("FM 4", 6, 4, 1), 4: ("FM 5", 6, 6, 2), 5: ("FM 6", 8, 6, 2)}
RATES = [(1, 2), (2, 3), (3, 4), (5, 6)]
BLOCK_BITS = 2880


def bits_per_symbol(speed):
    _, mid, edge, _ = SPEEDS[speed]
    return sum(edge if is_edge(k) else mid for k in DATA)


def blocks_per_frame(speed):
    return DATA_SYMBOLS * bits_per_symbol(speed) // BLOCK_BITS


def data_bits(speed):
    top, bottom = RATES[SPEEDS[speed][3]]
    return BLOCK_BITS * top // bottom


def piece_bytes(speed):
    return data_bits(speed) // 8 - 10


# ---- 5. Sync and pilots ----
def _table(name):
    with open(os.path.join(HERE, name)) as f:
        return [list(map(int, line.split())) for line in f if line.strip() and not line.startswith('#')]


SYNC, PILOT_BASE = {}, {}
for k, qs, qp in _table('sync-and-pilots.txt'):
    SYNC[k] = np.exp(1j * (np.pi / 4 + qs * np.pi / 2))
    PILOT_BASE[k] = np.exp(1j * (np.pi / 4 + qp * np.pi / 2))


def pilot_sign(s):
    return -1 if (0x5A3C96E1 >> (s % 32)) & 1 else 1


def pilot(k, s):
    return pilot_sign(s) * PILOT_BASE[k]


# ---- 7.2 Error correction (LDPC) ----
LDPC = {}


def ldpc_checks(rate_index):
    if rate_index not in LDPC:
        top, bottom = RATES[rate_index]
        LDPC[rate_index] = _table(f'ldpc-rate-{top}-{bottom}.txt')
    return LDPC[rate_index]


def ldpc_encode(rate_index, data):
    """The 2880-bit block: the data bits as they are, then the parity bits (a staircase)."""
    parity, before = [], 0
    for check in ldpc_checks(rate_index):
        p = before
        for i in check:
            p ^= data[i]
        parity.append(p)
        before = p
    return list(data) + parity


# ---- 7.3 Spreading: .NET's seeded System.Random (Appendix A) ----
class NetRandom:
    MBIG, MSEED = 2147483647, 161803398

    def __init__(self, seed):
        sa = [0] * 56
        mj = self.MSEED - abs(seed)
        sa[55] = mj
        mk, ii = 1, 0
        for _ in range(1, 55):
            ii = (ii + 21) % 55
            sa[ii] = mk
            mk = mj - mk
            if mk < 0:
                mk += self.MBIG
            mj = sa[ii]
        for _ in range(4):
            for i in range(1, 56):
                sa[i] -= sa[1 + (i + 30) % 55]
                if sa[i] < 0:
                    sa[i] += self.MBIG
        self.sa, self.inext, self.inextp = sa, 0, 21

    def _sample(self):
        self.inext = self.inext + 1 if self.inext < 55 else 1
        self.inextp = self.inextp + 1 if self.inextp < 55 else 1
        r = self.sa[self.inext] - self.sa[self.inextp]
        if r == self.MBIG:
            r -= 1
        if r < 0:
            r += self.MBIG
        self.sa[self.inext] = r
        return r

    def next(self, n):
        """A whole number from 0 to n-1, as .NET's Random.Next(n)."""
        return int(self._sample() * (1.0 / self.MBIG) * n)


def spread(speed):
    total = DATA_SYMBOLS * bits_per_symbol(speed)
    positions = list(range(total))
    rnd = NetRandom(9100 + speed)
    for i in range(total - 1, 0, -1):
        j = rnd.next(i + 1)
        positions[i], positions[j] = positions[j], positions[i]
    return positions


# ---- 7.4 Scrambling ----
def scrambler(count):
    state, bits = 0xACE1, []
    for _ in range(count):
        state ^= (state << 13) & 0xFFFFFFFF
        state ^= state >> 17
        state ^= (state << 5) & 0xFFFFFFFF
        bits.append(state & 1)
    return bits


# ---- 7.5 QAM ----
def qam(bits):
    half = len(bits) // 2
    levels = 1 << half
    scale = 1 / np.sqrt(2 * (levels * levels - 1) / 3)

    def level(part):
        gray = 0
        for b in part:
            gray = gray << 1 | b
        index, shift = gray, 1
        while shift < len(part):
            index ^= index >> shift
            shift <<= 1
        return 2 * index - (levels - 1)

    return complex(level(bits[:half]) * scale, level(bits[half:]) * scale)


# ---- 8.1 The file and its pieces (the fountain code) ----
EXP, LOG = [0] * 512, [0] * 256
_x = 1
for _i in range(255):
    EXP[_i] = EXP[_i + 255] = _x
    LOG[_x] = _i
    _x <<= 1
    if _x & 0x100:
        _x ^= 0x11D


def gf_mul(a, b):
    return 0 if a == 0 or b == 0 else EXP[LOG[a] + LOG[b]]


M64 = (1 << 64) - 1


def coefficients(r, k):
    """The k mixing numbers of repair piece r (SplitMix64, zero bytes skipped)."""
    state = (0x5246706978466173 ^ (r << 32) ^ k) & M64
    out = []
    while len(out) < k:
        state = (state + 0x9E3779B97F4A7C15) & M64
        z = state
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & M64
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & M64
        z ^= z >> 31
        if z >> 56:
            out.append(z >> 56)
    return out


def source_pieces(data, extension, size):
    k = -(-(8 + len(data)) // size)
    stream = bytearray(k * size)
    stream[0] = 1
    stream[1:4] = len(data).to_bytes(3, 'little')
    ext = extension.lstrip('.').lower().encode('latin-1')[:4]
    stream[4:4 + len(ext)] = ext
    stream[8:8 + len(data)] = data
    return [bytes(stream[i * size:(i + 1) * size]) for i in range(k)]


def repair_piece(source, r):
    out = bytearray(len(source[0]))
    for c, piece in zip(coefficients(r, len(source)), source):
        for j, v in enumerate(piece):
            out[j] ^= gf_mul(c, v)
    return bytes(out)


# ---- 8.3 The details block ----
INFO_NUMBER, REQUEST_NUMBER = 0xFFFF, 0xFFFE


def info_payload(callsign, name, size):
    call = callsign[:12].encode('ascii')
    nm = name.encode('utf-8')
    nm = nm[:min(size - 3 - len(call), 255)]
    p = bytearray(size)
    p[0] = 1
    p[1] = len(call)
    p[2:2 + len(call)] = call
    p[2 + len(call)] = len(nm)
    p[3 + len(call):3 + len(call) + len(nm)] = nm
    return bytes(p)


def has_info(f, frames):
    return f % 4 == 0 or f == frames - 1


# ---- 7.1 A block's data, and 8.2 the frames ----
def block_bits(file_number, k, number, payload, n_bits):
    b = bytearray(file_number.to_bytes(2, 'big') + k.to_bytes(2, 'big') + number.to_bytes(2, 'big') + payload)
    b += (zlib.crc32(bytes(b)) & 0xFFFFFFFF).to_bytes(4, 'big')
    assert len(b) * 8 == n_bits
    return [(b[i // 8] >> (7 - i % 8)) & 1 for i in range(n_bits)]


def symbol_sound(cells):
    """One symbol: x[n] = (2/N) Re sum X_k exp(+j 2 pi k n / N), the last 256 samples repeated in front."""
    spectrum = np.zeros(N, complex)
    for k, v in cells.items():
        spectrum[k] = v
    body = 2 * np.real(np.fft.ifft(spectrum))      # numpy's ifft already divides by N
    return np.concatenate([body[-GUARD:], body])


def frame_sound(speed, frame_number, blocks, file_number, k):
    """A frame's sound, from its blocks: (piece number, payload) pairs, blocks_per_frame of them."""
    rate_index = SPEEDS[speed][3]
    total = DATA_SYMBOLS * bits_per_symbol(speed)
    where, scr = spread(speed), scrambler(total)
    bits = [0] * total
    for b, (number, payload) in enumerate(blocks):
        block = ldpc_encode(rate_index, block_bits(file_number, k, number, payload, data_bits(speed)))
        for j, v in enumerate(block):
            bits[where[b * BLOCK_BITS + j]] = v
    bits = [a ^ s for a, s in zip(bits, scr)]

    symbols = [dict(SYNC), dict(SYNC)]
    header = speed << 8 | (frame_number & 0xFF)
    cells = {kk: pilot(kk, 2) for kk in PILOTS}
    for t, kk in enumerate(DATA):
        bit = (header >> (11 - t % 12)) & 1 if t < 96 else 0
        cells[kk] = -1.0 if bit else 1.0
    symbols.append(cells)
    _, mid, edge, _ = SPEEDS[speed]
    p = 0
    for s in range(DATA_SYMBOLS):
        cells = {kk: pilot(kk, 3 + s) for kk in PILOTS}
        for kk in DATA:
            n = edge if is_edge(kk) else mid
            cells[kk] = qam(bits[p:p + n])
            p += n
        symbols.append(cells)
    return np.concatenate([symbol_sound(c) for c in symbols])


def transmission(data, extension, speed, percent=20, file_number=1, callsign='N0CALL', name='picture.jpg'):
    """The whole sound for a file, before the level is set."""
    size, per = piece_bytes(speed), blocks_per_frame(speed)
    source = source_pieces(data, extension, size)
    k = len(source)
    count = k + (-(-k * percent // 100) if percent > 0 else 0)
    frames = max(1, -(-count // per))
    while frames * per - sum(has_info(f, frames) for f in range(frames)) < count:
        frames += 1
    slots = frames * per - sum(has_info(f, frames) for f in range(frames))
    info = info_payload(callsign, name, size)
    sound, number = [], 0

    def piece(n):
        return source[n] if n < k else repair_piece(source, n - k)

    for f in range(frames):
        blocks = [(INFO_NUMBER, info)] if has_info(f, frames) else []
        while len(blocks) < per:
            blocks.append((number, piece(number)))
            number += 1
        sound.append(frame_sound(speed, f, blocks, file_number, k))
    assert number == slots
    return np.concatenate(sound)


def write_wav(path, sound):
    sound = sound * (LEVEL / np.sqrt(np.mean(sound ** 2)))
    pcm = np.clip(np.round(sound * 32767), -32768, 32767).astype('<i2')
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm.tobytes())


def vectors():
    r = NetRandom(9102)
    print('Random(9102).Next(1000000) x5:', [r.next(1000000) for _ in range(5)])
    print('Spread FM 3, first 8:', spread(2)[:8])
    print('Scrambler, first 32 bits:', ''.join(map(str, scrambler(32))))
    print('Pilot signs, symbols 0-31:', ''.join('+' if pilot_sign(s) > 0 else '-' for s in range(32)))
    print('CRC-32 of "123456789": %08X' % (zlib.crc32(b'123456789') & 0xFFFFFFFF))
    print('Repair piece 0 of 7, its 7 mixing numbers:', coefficients(0, 7))
    print('GF(256): 0x57 * 0x83 = 0x%02X' % gf_mul(0x57, 0x83))
    for speed in SPEEDS:
        name, mid, edge, ri = SPEEDS[speed]
        print(f'{name}: {bits_per_symbol(speed)} bits a symbol, {blocks_per_frame(speed)} blocks a frame, '
              f'{piece_bytes(speed)}-byte pieces, rate {RATES[ri][0]}/{RATES[ri][1]}')
    data = bytes((i * 37 + 11) % 256 for i in range(1500))
    sound = transmission(data, 'jpg', 2, 20, 77, 'N0CALL', 'test.jpg')
    print('Example (section 11.3), FM 3: %d samples; sound[0:4] =' % len(sound), np.round(sound[:4] * 1e6) / 1e6)
    frame1 = sound[145152 + 3 * SYMBOL + GUARD:][:N]
    x = np.fft.fft(frame1)[[14, 15, 16]]    # the FFT gives back X_k exactly
    print('  frame 1, first data symbol, tones 14-16:', np.round(x, 6))


if __name__ == '__main__':
    ap = argparse.ArgumentParser(description='RFpix FM reference transmitter')
    ap.add_argument('input', nargs='?', help='the file to send (e.g. a .jpg)')
    ap.add_argument('output', nargs='?', help='the WAV file to write')
    ap.add_argument('--speed', type=int, default=3, choices=range(1, 7), help='FM 1 (toughest) to FM 6 (fastest); default 3')
    ap.add_argument('--fountain', type=int, default=20, help='extra repair pieces, percent; default 20')
    ap.add_argument('--call', default='N0CALL', help="the sender's callsign, sent in the details blocks")
    ap.add_argument('--file-number', type=int, default=1, help='1-65535, to tell your files apart; default 1')
    ap.add_argument('--vectors', action='store_true', help="print the spec's check values")
    a = ap.parse_args()
    if a.vectors or not a.input:
        vectors()
        sys.exit(0)
    if not a.output:
        ap.error('give an output .wav file')
    with open(a.input, 'rb') as f:
        data = f.read()
    ext = os.path.splitext(a.input)[1]
    sound = transmission(data, ext, a.speed - 1, a.fountain, a.file_number, a.call, os.path.basename(a.input))
    write_wav(a.output, sound)
    print(f'{a.output}: {len(sound) / RATE:.1f} seconds at FM {a.speed}')
