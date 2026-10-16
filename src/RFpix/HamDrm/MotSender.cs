// MotSender.cs - cuts a file into the data packets HamDRM sends, one packet per frame.
//
// The receiver's MotReceiver undoes all of this. The sender's steps, as QSSTV does them:
//   1. A small "header" describes the file: its size, its name and its type.
//   2. The file itself (the "body") is cut into equal pieces ("segments"), sized so
//      one piece fills exactly one frame. The header is a piece of its own.
//   3. Each piece is wrapped as a "data group": a few bytes saying what it is
//      (header or body, piece number, which file), the piece, and a checksum.
//   4. Each data group goes out as one packet: one more byte in front (first/last
//      flags, a counter), padding if the piece is short, and another checksum.
//
// The order the pieces go out in (QSSTV's "segment list", by ON4QZ):
//   - a run-in of 24 pieces: the header, the last body piece, then pieces 0, 1, 2...
//     A receiver that tunes in late still learns the file's name and size early.
//   - every body piece once, with the header and the last piece repeated every 50;
//   - a run-out of 10 pieces, like the run-in.
//   Then up to 5 more pieces from the start of the list, so the pieces spread over the
//   last frames by long interleaving still arrive whole. (QSSTV stops as soon as it has
//   made the fifth, so 4 of them go out.)
// Pieces sent twice give the receiver a second chance at any it missed.
//
// Ported from QSSTV's DABMOT.cpp, MOTSlideShow.cpp and DataDecoder.cpp; a test checks
// every packet against QSSTV's own transmitter, bit for bit.

using System.Text;
using RFpix.HamDrm.Coding;

namespace RFpix.HamDrm;

public sealed class MotSender
{
    public const int RunIn = 24;
    public const int RunOut = 10;
    public const int HeaderEvery = 50;
    public const int ExtraGroups = 5;
    public const int MaxNameLength = 80;
    private const int HeaderPieceSize = 98;   // bytes
    private const int PacketOverhead = 3;     // the packet's header byte and checksum
    private const int GroupOverhead = 11;     // the data group's own bytes around a piece

    private readonly byte[] _name;            // Latin-1, at most 80 characters
    private readonly List<byte[]> _headerPieces;
    private readonly List<byte[]> _bodyPieces;
    private int _headerCounter, _bodyCounter;   // data group counters (mod 16), per type

    /// <summary>The 16-bit number every data group carries to say which file it belongs to.
    /// QSSTV makes it from the file name, so a file sent again keeps its number.</summary>
    public int TransportId { get; }

    /// <summary>The order the pieces go out in: 0, 1, 2... are body pieces; -1 is the header.
    /// (Negative numbers count header pieces, but with names cut to 80 characters the header
    /// always fits in one.)</summary>
    public IReadOnlyList<int> Order { get; }

    /// <summary>Bytes of the file in each body piece.</summary>
    public int PieceSize { get; }

    public int BodyPieces => _bodyPieces.Count;

    /// <param name="name">The file name the receiver will save it as, e.g. "img04.jpg".</param>
    /// <param name="data">The file's bytes.</param>
    /// <param name="frameBytes">Whole bytes one frame carries (<see cref="MscDecoder.PacketBytes"/>).</param>
    public MotSender(string name, byte[] data, int frameBytes)
    {
        if (data.Length == 0) throw new ArgumentException("There's nothing to send: the file is empty.");
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("The file needs a name.");
        PieceSize = frameBytes - PacketOverhead - GroupOverhead;
        if (PieceSize < 1) throw new ArgumentException($"A frame of {frameBytes} bytes is too small to carry data.");

        _name = Encoding.Latin1.GetBytes(name.Length > MaxNameLength ? name[..MaxNameLength] : name);
        TransportId = TransportIdFor(_name);
        _headerPieces = Pieces(Header(data.Length), HeaderPieceSize);
        _bodyPieces = Pieces(data, PieceSize);
        Order = MakeOrder(_headerPieces.Count, _bodyPieces.Count);
    }

    /// <summary>QSSTV's "special hamcode": a sum and an exclusive-or of the name's characters.</summary>
    public static int TransportIdFor(byte[] name)
    {
        byte xor = 0, add = 0;
        for (int k = 0; k < name.Length; k++)
        {
            xor ^= name[k];
            add += name[k];
            add ^= (byte)k;
        }
        int id = 256 * add + xor;
        return id <= 2 ? id + name.Length : id;
    }

    /// <summary>Every frame's packet, in order, until QSSTV's sender would stop. Each is
    /// <c>frameBytes</c> long. As in QSSTV, the last one is made but never sent (see
    /// <see cref="HamDrmTransmitter"/>).</summary>
    public List<byte[]> Packets(int frameBytes)
    {
        int payload = frameBytes - PacketOverhead;
        var packets = new List<byte[]>();
        int listIndex = 0;
        bool allSent = false, stop = false;
        int extra = ExtraGroups;

        byte[] NextGroup()
        {
            if (allSent && extra-- <= 0) stop = true;
            var group = DataGroup(Order[listIndex]);
            if (++listIndex == Order.Count)
            {
                listIndex = 0;
                allSent = true;
            }
            return group;
        }

        var unit = NextGroup();
        int pointer = 0, counter = 0;
        while (true)
        {
            int remaining = unit.Length - pointer;
            bool last = remaining <= payload;
            var packet = new byte[frameBytes];
            packet[0] = (byte)((pointer == 0 ? 0x80 : 0) | (last ? 0x40 : 0) | (remaining < payload ? 0x08 : 0) | counter);
            counter = (counter + 1) % 8;
            if (remaining >= payload)
            {
                Array.Copy(unit, pointer, packet, 1, payload);
                pointer += payload;
            }
            else
            {
                packet[1] = (byte)remaining;   // a short piece: its length, then the piece, then zeros
                Array.Copy(unit, pointer, packet, 2, remaining);
            }
            if (last)
            {
                unit = NextGroup();
                pointer = 0;
            }
            int crc = Crc.Crc16(packet.AsSpan(0, frameBytes - 2));
            packet[^2] = (byte)(crc >> 8);
            packet[^1] = (byte)crc;
            packets.Add(packet);
            if (stop) return packets;
        }
    }

    // ---- the pieces ----

    private byte[] Header(int bodySize)
    {
        int headerSize = 7 + 5 + 3 + _name.Length + 2;
        var w = new BitWriter();
        w.Put(bodySize, 28);
        w.Put(headerSize, 13);
        w.Put(2, 6);                  // content type: image
        w.Put(ContentSubType(), 9);
        w.Put(2, 2); w.Put(5, 6); w.Put(0, 32);   // trigger time: "now"
        w.Put(1, 2); w.Put(6, 6); w.Put(0, 8);    // version number 0
        w.Put(3, 2); w.Put(12, 6);                // content name:
        w.Put(0, 1); w.Put(1 + _name.Length, 7);  //   its length,
        w.Put(0, 4); w.Put(0, 4);                 //   character set (Latin),
        foreach (byte c in _name) w.Put(c, 8);    //   the name
        return w.ToBytes();
    }

    private int ContentSubType()
    {
        string name = Encoding.Latin1.GetString(_name);
        string ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return ext switch { "gif" => 0, "bmp" => 2, "png" => 3, _ => 1 };   // 1 = JPEG (QSSTV's default)
    }

    /// <summary>Cuts bytes into pieces, each with its 2-byte piece header (its size).</summary>
    private static List<byte[]> Pieces(byte[] source, int size)
    {
        var pieces = new List<byte[]>();
        for (int at = 0; at < source.Length; at += size)
        {
            int n = Math.Min(size, source.Length - at);
            var piece = new byte[n + 2];
            piece[0] = (byte)(n >> 8);   // 3 bits "repetitions" (0), then 13 bits of size
            piece[1] = (byte)n;
            Array.Copy(source, at, piece, 2, n);
            pieces.Add(piece);
        }
        return pieces;
    }

    private static List<int> MakeOrder(int headers, int bodies)
    {
        var order = new List<int>();
        void HeaderAndLast()
        {
            for (int k = 0; k < headers; k++) order.Add(-1 - k);
            order.Add(bodies - 1);
        }
        void RunInOrOut(int length)
        {
            for (int counter = 0; counter < length;)
            {
                HeaderAndLast();
                counter++;
                for (int i = 0; i < bodies && counter < length; i++, counter++) order.Add(i);
            }
        }

        RunInOrOut(RunIn);
        for (int i = 0; i < bodies; i++)
        {
            if (i % HeaderEvery == 0) HeaderAndLast();
            order.Add(i);
        }
        RunInOrOut(RunOut);
        return order;
    }

    /// <summary>Wraps one piece as a data group (QSSTV's GenMOTObj).</summary>
    private byte[] DataGroup(int which)
    {
        bool header = which < 0;
        int number = header ? -1 - which : which;
        var piece = header ? _headerPieces[number] : _bodyPieces[number];
        bool lastPiece = number == (header ? _headerPieces.Count : _bodyPieces.Count) - 1;
        int counter = header ? _headerCounter++ % 16 : _bodyCounter++ % 16;

        var g = new byte[7 + piece.Length + 2];
        g[0] = (byte)(0x70 | (header ? 3 : 4));   // CRC, segment and user-access fields present; type
        g[1] = (byte)(counter << 4);              // counter; repetitions 0
        g[2] = (byte)((lastPiece ? 0x80 : 0) | (number >> 8));
        g[3] = (byte)number;
        g[4] = 0x12;                              // transport ID present, 2 bytes long
        g[5] = (byte)(TransportId >> 8);
        g[6] = (byte)TransportId;
        Array.Copy(piece, 0, g, 7, piece.Length);
        int crc = Crc.Crc16(g.AsSpan(0, g.Length - 2));
        g[^2] = (byte)(crc >> 8);
        g[^1] = (byte)crc;
        return g;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bits = new();
        public void Put(long value, int count)
        {
            for (int i = count - 1; i >= 0; i--) _bits.Add((byte)((value >> i) & 1));
        }
        public byte[] ToBytes()
        {
            var bytes = new byte[_bits.Count / 8];
            for (int i = 0; i < bytes.Length; i++)
                for (int b = 0; b < 8; b++) bytes[i] = (byte)((bytes[i] << 1) | _bits[8 * i + b]);
            return bytes;
        }
    }
}
