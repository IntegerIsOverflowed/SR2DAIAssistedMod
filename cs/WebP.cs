using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Sr2d64CSport
{
    /// <summary>
    /// WebP decoder (lossy VP8, lossless VP8L, ALPH alpha chunk, VP8X extended container) in pure managed
    /// code - no GDI+/WIC codec needed (Windows' System.Drawing cannot open .webp without the store
    /// "WebP Image Extensions", and even then not reliably). Output is straight (non-premultiplied)
    /// 0xAARRGGBB, top-down, like every other Sprite source.
    ///
    /// <code>
    /// using var s = Sprite.FromWebP("photo.webp");                  // file
    /// using var s = Sprite.FromWebP(bytes);                          // byte[] (embedded resource, network ...)
    /// var (w, h, argb) = WebP.Decode(bytes);                         // raw pixels
    /// bool isWebP = WebP.IsWebP(bytes);  var info = WebP.GetInfo(bytes);   // sniff / header only
    /// </code>
    ///
    /// Scope: still images. Animated files (ANIM/ANMF) decode their first frame; ICC / EXIF / XMP
    /// chunks are skipped. The decoder is a straight port of the libwebp reference decoder's
    /// arithmetic (same predictors, transforms, loop filter, "fancy" chroma upsampling and YUV
    /// matrix) so output matches libwebp / Chrome pixel for pixel. Malformed input throws
    /// <see cref="InvalidDataException"/>; nothing is read outside the given buffer.
    /// Speed (single thread, .NET 9 x64): ~4 ms for a 512² lossy, ~3 ms for a 512² lossless picture.
    /// </summary>
    internal static class WebP
    {
        public readonly struct Info
        {
            public readonly int Width, Height; public readonly bool HasAlpha, Lossless, Animated;
            public Info(int w, int h, bool alpha, bool lossless, bool animated) { Width = w; Height = h; HasAlpha = alpha; Lossless = lossless; Animated = animated; }
            public override string ToString() => $"{Width}x{Height} {(Lossless ? "lossless" : "lossy")}{(HasAlpha ? " alpha" : "")}{(Animated ? " animated" : "")}";
        }

        /// <summary>True for a RIFF/WEBP signature (12 bytes checked).</summary>
        public static bool IsWebP(ReadOnlySpan<byte> d) => d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P';

        /// <summary>Header information without decoding the pixels; null when not a WebP file.</summary>
        public static Info? GetInfo(ReadOnlySpan<byte> d)
        {
            if (!IsWebP(d)) return null;
            try { var c = ParseContainer(d.ToArray()); return new Info(c.Width, c.Height, c.HasAlpha, c.Lossless, c.Animated); }
            catch (InvalidDataException) { return null; }
            catch (IndexOutOfRangeException) { return null; }
        }

        /// <summary>Decodes a WebP file into straight ARGB pixels (row-major, top-down).</summary>
        public static (int Width, int Height, int[] Argb) Decode(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            try { return DecodeCore(data); }
            catch (IndexOutOfRangeException) { throw Bad("truncated or corrupt bitstream"); }   // a chunk length that lies about its payload
        }
        static (int Width, int Height, int[] Argb) DecodeCore(byte[] data)
        {
            var c = ParseContainer(data);
            int[] argb;
            if (c.Lossless)
            {
                argb = new Vp8lDecoder(data, c.BitstreamOffset, c.BitstreamLength).DecodeImage(out int w, out int h);
                if (w != c.Width || h != c.Height) { c.Width = w; c.Height = h; }
            }
            else
            {
                var dec = new Vp8Decoder(data, c.BitstreamOffset, c.BitstreamLength);
                argb = dec.Decode(out int w, out int h);
                if (w != c.Width || h != c.Height) { c.Width = w; c.Height = h; }
                if (c.AlphaLength > 0) ApplyAlpha(argb, w, h, data, c.AlphaOffset, c.AlphaLength);
            }
            return (c.Width, c.Height, argb);
        }
        public static (int Width, int Height, int[] Argb) Decode(Stream s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            using var ms = new MemoryStream(); s.CopyTo(ms); return Decode(ms.ToArray());
        }
        public static (int Width, int Height, int[] Argb) Decode(string file) => Decode(File.ReadAllBytes(file));

        // ------------------------------------------------------------------------------------------- container
        sealed class Container
        {
            public int Width, Height; public bool HasAlpha, Lossless, Animated;
            public int BitstreamOffset, BitstreamLength, AlphaOffset, AlphaLength;
        }
        static uint LE32(byte[] d, int p) => (uint)(d[p] | d[p + 1] << 8 | d[p + 2] << 16 | d[p + 3] << 24);
        static int LE24(byte[] d, int p) => d[p] | d[p + 1] << 8 | d[p + 2] << 16;
        static bool Tag(byte[] d, int p, string t) => p + 4 <= d.Length && d[p] == t[0] && d[p + 1] == t[1] && d[p + 2] == t[2] && d[p + 3] == t[3];
        static InvalidDataException Bad(string why) => new InvalidDataException("webp: " + why);

        static Container ParseContainer(byte[] d)
        {
            if (!IsWebP(d)) throw Bad("not a RIFF/WEBP file");
            long riffSize = LE32(d, 4); if (riffSize < 12) throw Bad("bad RIFF size");
            int end = (int)Math.Min(d.Length, 8 + riffSize);
            var c = new Container();
            int p = 12; bool vp8x = false;
            if (Tag(d, p, "VP8X"))
            {
                if (LE32(d, p + 4) != 10 || p + 18 > end) throw Bad("bad VP8X chunk");
                uint flags = LE32(d, p + 8);
                c.HasAlpha = (flags & 0x10) != 0; c.Animated = (flags & 0x02) != 0;
                c.Width = 1 + LE24(d, p + 12); c.Height = 1 + LE24(d, p + 15);
                p += 18; vp8x = true;
            }
            // walk the chunks: ALPH before the bitstream; ANIM/ANMF for animations (first frame only)
            while (true)
            {
                if (p + 8 > end) throw Bad("no image bitstream (truncated?)");
                long size = LE32(d, p + 4);
                int payload = p + 8; long next = payload + size + (size & 1);
                if (Tag(d, p, "VP8 ") || Tag(d, p, "VP8L"))
                {
                    if (payload + size > end) size = end - payload;
                    c.Lossless = d[p + 3] == 'L'; c.BitstreamOffset = payload; c.BitstreamLength = (int)size;
                    break;
                }
                if (Tag(d, p, "ALPH")) { c.AlphaOffset = payload; c.AlphaLength = (int)Math.Max(0, Math.Min(size, end - payload)); }
                else if (Tag(d, p, "ANMF"))
                {   // frame header (16 bytes) then the frame's own sub-chunks: descend into it
                    c.Animated = true;
                    if (payload + 16 > end) throw Bad("bad ANMF chunk");
                    end = (int)Math.Min(end, next);
                    p = payload + 16; continue;
                }
                if (next > end) throw Bad("chunk runs past the file");
                p = (int)next;
            }
            // dimensions from the bitstream header itself (authoritative; VP8X may be absent)
            if (c.Lossless)
            {
                if (c.BitstreamLength < 5 || d[c.BitstreamOffset] != 0x2f) throw Bad("bad VP8L header");
                uint b = LE32(d, c.BitstreamOffset + 1);
                c.Width = (int)(b & 0x3fff) + 1; c.Height = (int)((b >> 14) & 0x3fff) + 1; c.HasAlpha |= ((b >> 28) & 1) != 0;
                if (((b >> 29) & 7) != 0) throw Bad("unsupported VP8L version");
            }
            else
            {
                int o = c.BitstreamOffset;
                if (c.BitstreamLength < 10 || d[o + 3] != 0x9d || d[o + 4] != 0x01 || d[o + 5] != 0x2a) throw Bad("bad VP8 frame header");
                c.Width = (d[o + 6] | d[o + 7] << 8) & 0x3fff; c.Height = (d[o + 8] | d[o + 9] << 8) & 0x3fff;
                c.HasAlpha |= c.AlphaLength > 0;
            }
            if (c.Width <= 0 || c.Height <= 0) throw Bad("zero size");
            if ((long)c.Width * c.Height > 1L << 28) throw Bad("image too large");
            _ = vp8x;
            return c;
        }

        // ------------------------------------------------------------------------------------------- ALPH chunk
        static void ApplyAlpha(int[] argb, int w, int h, byte[] d, int off, int len)
        {
            if (len < 1) return;
            int hdr = d[off]; int method = hdr & 3, filter = (hdr >> 2) & 3, pre = (hdr >> 4) & 3;
            if (method > 1 || (hdr >> 6) != 0) throw Bad("bad ALPH header");
            _ = pre;
            var alpha = new byte[w * h];
            if (method == 0)
            {
                if (len - 1 < w * h) throw Bad("ALPH chunk too short");
                Buffer.BlockCopy(d, off + 1, alpha, 0, w * h);
            }
            else
            {   // lossless-coded: the alpha values are the green channel of a VP8L image stream (no header)
                var dec = new Vp8lDecoder(d, off + 1, len - 1, headerless: true, width: w, height: h);
                dec.DecodeAlpha(alpha);
            }
            // unfilter in place (predictors read the already unfiltered neighbours)
            if (filter != 0)
            {
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    if (y == 0 || filter == 1)
                    {   // horizontal: first pixel predicted from the pixel above (0 on row 0)
                        int pred = y == 0 ? 0 : alpha[row - w];
                        for (int x = 0; x < w; x++) { pred = (byte)(pred + alpha[row + x]); alpha[row + x] = (byte)pred; }
                    }
                    else if (filter == 2)
                    {
                        for (int x = 0; x < w; x++) alpha[row + x] = (byte)(alpha[row - w + x] + alpha[row + x]);
                    }
                    else
                    {   // gradient
                        int top = alpha[row - w], topLeft = top, left = top;
                        for (int x = 0; x < w; x++)
                        {
                            top = alpha[row - w + x];
                            int g = left + top - topLeft; g = g < 0 ? 0 : g > 255 ? 255 : g;
                            left = (byte)(alpha[row + x] + g); topLeft = top; alpha[row + x] = (byte)left;
                        }
                    }
                }
            }
            for (int i = 0; i < argb.Length; i++) argb[i] = (argb[i] & 0xffffff) | alpha[i] << 24;
        }

        // =========================================================================================== VP8L (lossless)
        struct HuffmanCode { public byte Bits; public ushort Value; }

        /// <summary>LSB-first bit reader of the lossless format (64-bit window, reads past the end yield zeros and set Eos).</summary>
        sealed class LBitReader
        {
            readonly byte[] d; int pos; readonly int end; ulong val; int bitPos; public bool Eos;
            public LBitReader(byte[] data, int off, int len)
            {
                d = data; pos = off; end = off + len;
                int n = Math.Min(8, len); for (int i = 0; i < n; i++) val |= (ulong)d[pos++] << (8 * i);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)] public uint Prefetch() => (uint)(val >> (bitPos & 63));
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Advance(int n) { bitPos += n; Shift(); }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void Shift()
            {
                if (bitPos < 8) return;
                if (bitPos >= 32 && pos + 4 <= end)
                {   // refill four bytes at once
                    val = (val >> 32) | ((ulong)(d[pos] | d[pos + 1] << 8 | d[pos + 2] << 16 | d[pos + 3] << 24) << 32);
                    pos += 4; bitPos -= 32;
                }
                while (bitPos >= 8 && pos < end) { val = (val >> 8) | ((ulong)d[pos++] << 56); bitPos -= 8; }
                if (pos == end && bitPos > 64) Eos = true;
            }
            public bool IsEnd { get { Shift(); return Eos || (pos == end && bitPos > 64); } }
            public uint ReadBits(int n)
            {
                if (Eos || n > 24) { Eos = true; return 0; }
                uint v = Prefetch() & ((1u << n) - 1); bitPos += n; Shift(); return v;
            }
            /// <summary>Reads one symbol (up to 15 bits) from the window WITHOUT refilling: call <see cref="Fill"/> so that at least 30 bits are available before two of these.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int ReadSymbol(HuffmanCode[] t)
            {
                uint v = Prefetch(); int i = (int)(v & 0xff);
                ref HuffmanCode e = ref t[i]; int nbits = e.Bits - 8;
                if (nbits > 0)
                {
                    bitPos += 8; v = Prefetch();
                    i += e.Value; i += (int)(v & ((1u << nbits) - 1));
                    e = ref t[i];
                }
                bitPos += e.Bits;
                return e.Value;
            }
            /// <summary>Refills the 64-bit window (at least 56 valid bits afterwards while data lasts).</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)] public void Fill() => Shift();
        }

        sealed class HTreeGroup { public HuffmanCode[][] Trees = new HuffmanCode[5][]; }
        sealed class LTransform { public int Type, Bits, XSize, YSize; public uint[]? Data; }

        sealed class Vp8lDecoder
        {
            const int NumLiteralCodes = 256, NumLengthCodes = 24, NumDistanceCodes = 40, CodeLengthCodes = 19, MaxCodeLength = 15;
            static readonly byte[] CodeLengthCodeOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
            static readonly int[] AlphabetSizes = { NumLiteralCodes + NumLengthCodes, NumLiteralCodes, NumLiteralCodes, NumLiteralCodes, NumDistanceCodes };
            readonly LBitReader br; readonly bool headerless; int width, height;
            readonly LTransform[] transforms = new LTransform[4]; int numTransforms; uint transformsSeen;
            long tableBudget = 16L << 20;      // table entries (4 B each); guards against hostile group counts

            public Vp8lDecoder(byte[] data, int off, int len, bool headerless = false, int width = 0, int height = 0)
            {
                br = new LBitReader(data, off, len); this.headerless = headerless; this.width = width; this.height = height;
            }

            public int[] DecodeImage(out int w, out int h)
            {
                if (!headerless)
                {
                    if (br.ReadBits(8) != 0x2f) throw Bad("bad VP8L signature");
                    width = (int)br.ReadBits(14) + 1; height = (int)br.ReadBits(14) + 1;
                    br.ReadBits(1);                                   // alpha hint
                    if (br.ReadBits(3) != 0) throw Bad("unsupported VP8L version");
                }
                w = width; h = height;
                var data = DecodeImageStream(width, height, true);
                var argb = new int[width * height];
                Buffer.BlockCopy(data, 0, argb, 0, argb.Length * 4);
                return argb;
            }
            /// <summary>ALPH chunk payload: the alpha values are the green channel of a headerless image stream.</summary>
            public void DecodeAlpha(byte[] alpha)
            {
                var data = DecodeImageStream(width, height, true);
                for (int i = 0; i < alpha.Length; i++) alpha[i] = (byte)(data[i] >> 8);
            }

            // ---- streams
            uint[] DecodeImageStream(int xsize, int ysize, bool level0)
            {
                int txsize = xsize;
                if (level0) while (br.ReadBits(1) != 0) ReadTransform(ref txsize, ysize);
                int cacheBits = 0;
                if (br.ReadBits(1) != 0) { cacheBits = (int)br.ReadBits(4); if (cacheBits < 1 || cacheBits > 11) throw Bad("bad colour cache size"); }
                ReadHuffmanCodes(txsize, ysize, cacheBits, level0, out var groups, out var huffImage, out int huffBits, out int huffXSize);
                if (br.Eos) throw Bad("truncated lossless stream");
                var data = new uint[(long)txsize * ysize];
                DecodeImageData(data, txsize, ysize, cacheBits, groups, huffImage, huffBits, huffXSize);
                if (level0 && numTransforms > 0) data = ApplyInverseTransforms(data, txsize, ysize);
                return data;
            }

            void ReadTransform(ref int xsize, int ysize)
            {
                int type = (int)br.ReadBits(2);
                if ((transformsSeen & (1u << type)) != 0) throw Bad("duplicate transform");
                transformsSeen |= 1u << type;
                if (numTransforms >= 4) throw Bad("too many transforms");
                var t = new LTransform { Type = type, XSize = xsize, YSize = ysize };
                transforms[numTransforms++] = t;
                switch (type)
                {
                    case 0: case 1:                                   // predictor / cross colour
                        t.Bits = 2 + (int)br.ReadBits(3);
                        t.Data = DecodeImageStream(SubSampleSize(xsize, t.Bits), SubSampleSize(ysize, t.Bits), false);
                        break;
                    case 3:                                           // colour indexing
                    {
                        int numColors = (int)br.ReadBits(8) + 1;
                        int bits = numColors > 16 ? 0 : numColors > 4 ? 1 : numColors > 2 ? 2 : 3;
                        xsize = SubSampleSize(xsize, bits); t.Bits = bits;
                        var pal = DecodeImageStream(numColors, 1, false);
                        int finalColors = 1 << (8 >> bits);
                        var map = new uint[finalColors];
                        // palette entries are delta coded per byte
                        var bytes = new byte[4 * finalColors];
                        for (int i = 0; i < 4; i++) bytes[i] = (byte)(pal[0] >> (8 * i));
                        for (int i = 4; i < 4 * numColors; i++) bytes[i] = (byte)(((pal[i >> 2] >> (8 * (i & 3))) & 0xff) + bytes[i - 4]);
                        for (int i = 0; i < finalColors; i++) map[i] = (uint)(bytes[4 * i] | bytes[4 * i + 1] << 8 | bytes[4 * i + 2] << 16 | bytes[4 * i + 3] << 24);
                        t.Data = map;
                        break;
                    }
                    case 2: break;                                    // subtract green
                }
            }
            static int SubSampleSize(int size, int bits) => (size + (1 << bits) - 1) >> bits;

            // ---- Huffman
            void ReadHuffmanCodes(int xsize, int ysize, int cacheBits, bool allowRecursion, out HTreeGroup[] groups, out uint[]? huffImage, out int huffBits, out int huffXSize)
            {
                huffImage = null; huffBits = 0; huffXSize = 0; int numGroups = 1;
                if (allowRecursion && br.ReadBits(1) != 0)
                {
                    huffBits = 2 + (int)br.ReadBits(3);
                    huffXSize = SubSampleSize(xsize, huffBits); int hy = SubSampleSize(ysize, huffBits);
                    huffImage = DecodeImageStream(huffXSize, hy, false);
                    for (int i = 0; i < huffImage.Length; i++) { int g = (int)((huffImage[i] >> 8) & 0xffff); huffImage[i] = (uint)g; if (g >= numGroups) numGroups = g + 1; }
                }
                if (br.Eos) throw Bad("truncated lossless stream");
                // groups that no tile references are still present in the stream: read and drop them
                bool[]? used = null;
                if (huffImage != null && numGroups > huffImage.Length) { used = new bool[numGroups]; foreach (var g in huffImage) used[g] = true; }
                int maxAlphabet = AlphabetSizes[0] + (cacheBits > 0 ? 1 << cacheBits : 0);
                var codeLengths = new int[maxAlphabet];
                var scratch = new HuffmanCode[1 << 16];
                groups = new HTreeGroup[numGroups];
                for (int g = 0; g < numGroups; g++)
                {
                    bool keep = used == null || used[g];
                    var grp = keep ? new HTreeGroup() : null;
                    for (int j = 0; j < 5; j++)
                    {
                        int alphabet = AlphabetSizes[j] + (j == 0 && cacheBits > 0 ? 1 << cacheBits : 0);
                        int size = ReadHuffmanCode(alphabet, codeLengths, scratch);
                        if (grp != null)
                        {
                            tableBudget -= size; if (tableBudget < 0) throw Bad("too many Huffman tables");
                            var t = new HuffmanCode[size]; Array.Copy(scratch, t, size); grp.Trees[j] = t;
                        }
                    }
                    if (grp != null) groups[g] = grp;
                }
                if (used != null) { var any = Array.Find(groups, x => x != null)!; for (int g = 0; g < numGroups; g++) groups[g] ??= any; }
            }

            int ReadHuffmanCode(int alphabetSize, int[] codeLengths, HuffmanCode[] table)
            {
                Array.Clear(codeLengths, 0, codeLengths.Length);
                if (br.ReadBits(1) != 0)
                {   // simple code: 1 or 2 symbols
                    int numSymbols = (int)br.ReadBits(1) + 1;
                    int firstLen = (int)br.ReadBits(1);
                    int symbol = (int)br.ReadBits(firstLen == 0 ? 1 : 8);
                    codeLengths[symbol] = 1;
                    if (numSymbols == 2) { symbol = (int)br.ReadBits(8); codeLengths[symbol] = 1; }
                }
                else
                {
                    var clcl = new int[CodeLengthCodes];
                    int numCodes = (int)br.ReadBits(4) + 4;
                    if (numCodes > CodeLengthCodes) throw Bad("bad code length count");
                    for (int i = 0; i < numCodes; i++) clcl[CodeLengthCodeOrder[i]] = (int)br.ReadBits(3);
                    ReadHuffmanCodeLengths(clcl, alphabetSize, codeLengths);
                }
                if (br.Eos) throw Bad("truncated Huffman code");
                int size = BuildHuffmanTable(table, 8, codeLengths, alphabetSize);
                if (size <= 0) throw Bad("invalid Huffman code");
                return size;
            }

            void ReadHuffmanCodeLengths(int[] clcl, int numSymbols, int[] codeLengths)
            {
                var table = new HuffmanCode[1 << 7];
                if (BuildHuffmanTable(table, 7, clcl, CodeLengthCodes) <= 0) throw Bad("invalid code length code");
                int maxSymbol;
                if (br.ReadBits(1) != 0)
                {
                    int lengthNBits = 2 + 2 * (int)br.ReadBits(3);
                    maxSymbol = 2 + (int)br.ReadBits(lengthNBits);
                    if (maxSymbol > numSymbols) throw Bad("bad max symbol");
                }
                else maxSymbol = numSymbols;
                int symbol = 0, prevLen = 8;
                while (symbol < numSymbols)
                {
                    if (maxSymbol-- == 0) break;
                    br.Fill();
                    int idx = (int)(br.Prefetch() & 127); br.Advance(table[idx].Bits); int codeLen = table[idx].Value;
                    if (codeLen < 16) { codeLengths[symbol++] = codeLen; if (codeLen != 0) prevLen = codeLen; }
                    else
                    {
                        int slot = codeLen - 16;
                        int extra = slot == 0 ? 2 : slot == 1 ? 3 : 7, offset = slot == 2 ? 11 : 3;
                        int repeat = (int)br.ReadBits(extra) + offset;
                        if (symbol + repeat > numSymbols) throw Bad("code length repeat overflow");
                        int len = codeLen == 16 ? prevLen : 0;
                        while (repeat-- > 0) codeLengths[symbol++] = len;
                    }
                    if (br.Eos) throw Bad("truncated code lengths");
                }
            }

            static uint NextKey(uint key, int len)
            {
                uint step = 1u << (len - 1);
                while ((key & step) != 0) step >>= 1;
                return step != 0 ? (key & (step - 1)) + step : key;
            }
            static void Replicate(HuffmanCode[] table, int at, int step, int end, HuffmanCode code)
            {
                do { end -= step; table[at + end] = code; } while (end > 0);
            }
            static int NextTableBitSize(int[] count, int len, int rootBits)
            {
                int left = 1 << (len - rootBits);
                while (len < MaxCodeLength) { left -= count[len]; if (left <= 0) break; ++len; left <<= 1; }
                return len - rootBits;
            }
            /// <summary>libwebp's two-level table builder (root table + second-level tables). Returns the total size, 0 for an invalid code.</summary>
            static int BuildHuffmanTable(HuffmanCode[] table, int rootBits, int[] codeLengths, int n)
            {
                int totalSize = 1 << rootBits;
                var count = new int[MaxCodeLength + 1]; var offset = new int[MaxCodeLength + 1];
                for (int s = 0; s < n; s++) { if (codeLengths[s] > MaxCodeLength) return 0; count[codeLengths[s]]++; }
                if (count[0] == n) return 0;
                offset[1] = 0;
                for (int len = 1; len < MaxCodeLength; len++) { if (count[len] > (1 << len)) return 0; offset[len + 1] = offset[len] + count[len]; }
                var sorted = new ushort[n];
                for (int s = 0; s < n; s++) { int l = codeLengths[s]; if (l > 0) { if (offset[l] >= n) return 0; sorted[offset[l]++] = (ushort)s; } }
                if (offset[MaxCodeLength] == 1)
                {   // one symbol: zero-length code
                    Replicate(table, 0, 1, totalSize, new HuffmanCode { Bits = 0, Value = sorted[0] });
                    return totalSize;
                }
                int tbl = 0, tableBits = rootBits, tableSize = 1 << rootBits, symbol = 0, numNodes = 1, numOpen = 1, step; uint low = 0xffffffff, mask = (uint)totalSize - 1, key = 0;
                for (int len = 1, st = 2; len <= rootBits; len++, st <<= 1)
                {
                    numOpen <<= 1; numNodes += numOpen; numOpen -= count[len]; if (numOpen < 0) return 0;
                    for (; count[len] > 0; count[len]--)
                    {
                        Replicate(table, (int)key, st, tableSize, new HuffmanCode { Bits = (byte)len, Value = sorted[symbol++] });
                        key = NextKey(key, len);
                    }
                }
                for (int len = rootBits + 1; len <= MaxCodeLength; len++)
                {
                    step = 1 << (len - rootBits);
                    numOpen <<= 1; numNodes += numOpen; numOpen -= count[len]; if (numOpen < 0) return 0;
                    for (; count[len] > 0; count[len]--)
                    {
                        if ((key & mask) != low)
                        {
                            tbl += tableSize;
                            tableBits = NextTableBitSize(count, len, rootBits); tableSize = 1 << tableBits; totalSize += tableSize;
                            if (totalSize > table.Length) return 0;
                            low = key & mask;
                            table[low] = new HuffmanCode { Bits = (byte)(tableBits + rootBits), Value = (ushort)(tbl - low) };
                        }
                        Replicate(table, tbl + (int)(key >> rootBits), step, tableSize, new HuffmanCode { Bits = (byte)(len - rootBits), Value = sorted[symbol++] });
                        key = NextKey(key, len);
                    }
                }
                if (numNodes != 2 * offset[MaxCodeLength] - 1) return 0;
                return totalSize;
            }

            // ---- pixels
            int GetCopyDistance(int sym)
            {
                if (sym < 4) return sym + 1;
                int extraBits = (sym - 2) >> 1; int offset = (2 + (sym & 1)) << extraBits;
                return offset + (int)br.ReadBits(extraBits) + 1;
            }
            static int PlaneCodeToDistance(int xsize, int planeCode)
            {
                if (planeCode > 120) return planeCode - 120;
                int dist = CodeToPlane[planeCode - 1]; int yoff = dist >> 4, xoff = 8 - (dist & 0xf);
                int d = yoff * xsize + xoff; return d >= 1 ? d : 1;
            }

            unsafe void DecodeImageData(uint[] dataArr, int width, int height, int cacheBits, HTreeGroup[] groups, uint[]? huffImage, int huffBits, int huffXSize)
            {
                int total = width * height, src = 0, col = 0, row = 0;
                int lenCodeLimit = NumLiteralCodes + NumLengthCodes, cacheLimit = lenCodeLimit + (cacheBits > 0 ? 1 << cacheBits : 0);
                uint[]? cache = cacheBits > 0 ? new uint[1 << cacheBits] : null; int cacheShift = 32 - cacheBits; int lastCached = 0;
                int mask = huffBits == 0 ? ~0 : (1 << huffBits) - 1;
                HTreeGroup group = groups[huffImage == null ? 0 : (int)huffImage[0]];
                var tG = group.Trees[0]; var tR = group.Trees[1]; var tB = group.Trees[2]; var tA = group.Trees[3];
                fixed (uint* data = dataArr)
                {
                    while (src < total)
                    {
                        if ((col & mask) == 0 && huffImage != null)
                        {
                            group = groups[(int)huffImage[huffXSize * (row >> huffBits) + (col >> huffBits)]];
                            tG = group.Trees[0]; tR = group.Trees[1]; tB = group.Trees[2]; tA = group.Trees[3];
                        }
                        br.Fill();
                        int code = br.ReadSymbol(tG);
                        if (code < NumLiteralCodes)
                        {
                            int red = br.ReadSymbol(tR);
                            br.Fill();
                            int blue = br.ReadSymbol(tB);
                            int alpha = br.ReadSymbol(tA);
                            if (br.IsEnd) break;
                            data[src] = (uint)alpha << 24 | (uint)red << 16 | (uint)code << 8 | (uint)blue;
                            ++src; ++col;
                            if (col >= width)
                            {
                                col = 0; ++row;
                                if (cache != null) while (lastCached < src) { uint c = data[lastCached++]; cache[(c * 0x1e35a7bdu) >> cacheShift] = c; }
                            }
                        }
                        else if (code < lenCodeLimit)
                        {
                            int length = GetCopyDistance(code - NumLiteralCodes);
                            br.Fill();
                            int distSym = br.ReadSymbol(group.Trees[4]);
                            br.Fill();
                            int distCode = GetCopyDistance(distSym);
                            int dist = PlaneCodeToDistance(width, distCode);
                            if (br.IsEnd) break;
                            if (src < dist || total - src < length) throw Bad("bad backward reference");
                            uint* p = data + src, q = p - dist;
                            if (dist >= length) Buffer.MemoryCopy(q, p, (long)length * 4, (long)length * 4);
                            else for (int i = 0; i < length; i++) p[i] = q[i];                 // overlapping pattern copy
                            src += length; col += length;
                            while (col >= width) { col -= width; ++row; }
                            if ((col & mask) != 0 && huffImage != null)
                            {
                                group = groups[(int)huffImage[huffXSize * (row >> huffBits) + (col >> huffBits)]];
                                tG = group.Trees[0]; tR = group.Trees[1]; tB = group.Trees[2]; tA = group.Trees[3];
                            }
                            if (cache != null) while (lastCached < src) { uint c = data[lastCached++]; cache[(c * 0x1e35a7bdu) >> cacheShift] = c; }
                        }
                        else if (code < cacheLimit)
                        {
                            int key = code - lenCodeLimit;
                            if (br.IsEnd) break;
                            while (lastCached < src) { uint c = data[lastCached++]; cache![(c * 0x1e35a7bdu) >> cacheShift] = c; }
                            data[src] = cache![key];
                            ++src; ++col;
                            if (col >= width)
                            {
                                col = 0; ++row;
                                while (lastCached < src) { uint c = data[lastCached++]; cache[(c * 0x1e35a7bdu) >> cacheShift] = c; }
                            }
                        }
                        else throw Bad("bad symbol");
                    }
                }
                if (br.IsEnd && src < total) throw Bad("truncated lossless image data");
            }

            // ---- inverse transforms
            uint[] ApplyInverseTransforms(uint[] data, int width, int height)
            {
                for (int n = numTransforms - 1; n >= 0; n--)
                {
                    var t = transforms[n]; var o = new uint[(long)t.XSize * t.YSize];
                    switch (t.Type)
                    {
                        case 0: PredictorInverse(t, data, o); break;
                        case 1: ColorSpaceInverse(t, data, o); break;
                        case 2: for (int i = 0; i < data.Length; i++) { uint a = data[i], g = (a >> 8) & 0xff; uint rb = (a & 0x00ff00ffu) + ((g << 16) | g); o[i] = (a & 0xff00ff00u) | (rb & 0x00ff00ffu); } break;
                        case 3: ColorIndexInverse(t, data, o); break;
                    }
                    data = o;
                }
                return data;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static uint AddPixels(uint a, uint b)
            {
                uint ag = (a & 0xff00ff00u) + (b & 0xff00ff00u), rb = (a & 0x00ff00ffu) + (b & 0x00ff00ffu);
                return (ag & 0xff00ff00u) | (rb & 0x00ff00ffu);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)] static uint Average2(uint a, uint b) => (((a ^ b) & 0xfefefefeu) >> 1) + (a & b);
            static uint Clip255(uint a) => a < 256 ? a : ~a >> 24;
            static uint ClampedAddSubtractFull(uint c0, uint c1, uint c2)
            {
                uint a = Clip255((uint)((int)(c0 >> 24) + (int)(c1 >> 24) - (int)(c2 >> 24)));
                uint r = Clip255((uint)((int)((c0 >> 16) & 0xff) + (int)((c1 >> 16) & 0xff) - (int)((c2 >> 16) & 0xff)));
                uint g = Clip255((uint)((int)((c0 >> 8) & 0xff) + (int)((c1 >> 8) & 0xff) - (int)((c2 >> 8) & 0xff)));
                uint b = Clip255((uint)((int)(c0 & 0xff) + (int)(c1 & 0xff) - (int)(c2 & 0xff)));
                return a << 24 | r << 16 | g << 8 | b;
            }
            static uint AddSubtractHalf(uint a, uint b) => Clip255((uint)((int)a + ((int)a - (int)b) / 2));
            static uint ClampedAddSubtractHalf(uint c0, uint c1, uint c2)
            {
                uint ave = Average2(c0, c1);
                return AddSubtractHalf(ave >> 24, c2 >> 24) << 24 | AddSubtractHalf((ave >> 16) & 0xff, (c2 >> 16) & 0xff) << 16
                     | AddSubtractHalf((ave >> 8) & 0xff, (c2 >> 8) & 0xff) << 8 | AddSubtractHalf(ave & 0xff, c2 & 0xff);
            }
            static int Sub3(int a, int b, int c) => Math.Abs(b - c) - Math.Abs(a - c);
            static uint Select(uint a, uint b, uint c)
            {
                int d = Sub3((int)(a >> 24), (int)(b >> 24), (int)(c >> 24)) + Sub3((int)(a >> 16) & 0xff, (int)(b >> 16) & 0xff, (int)(c >> 16) & 0xff)
                      + Sub3((int)(a >> 8) & 0xff, (int)(b >> 8) & 0xff, (int)(c >> 8) & 0xff) + Sub3((int)a & 0xff, (int)b & 0xff, (int)c & 0xff);
                return d <= 0 ? a : b;
            }
            static void PredictorInverse(LTransform t, uint[] i, uint[] o)
            {
                int width = t.XSize, height = t.YSize;
                if (width <= 0 || height <= 0) return;
                o[0] = AddPixels(i[0], 0xff000000u);
                for (int x = 1; x < width; x++) o[x] = AddPixels(i[x], o[x - 1]);
                int bits = t.Bits, tileW = 1 << bits, tilesPerRow = SubSampleSize(width, bits); var modes = t.Data!;
                for (int y = 1; y < height; y++)
                {
                    int row = y * width, modeRow = (y >> bits) * tilesPerRow;
                    o[row] = AddPixels(i[row], o[row - width]);
                    for (int x = 1; x < width;)
                    {
                        int mode = (int)((modes[modeRow + (x >> bits)] >> 8) & 0xf);
                        int xEnd = Math.Min(width, (x & ~(tileW - 1)) + tileW);
                        PredictRun(mode, i, o, row + x, xEnd - x, width);
                        x = xEnd;
                    }
                }
            }
            /// <summary>o[idx .. idx+n) = i[..] + predictor(mode) - one switch per tile run instead of per pixel.</summary>
            static void PredictRun(int mode, uint[] i, uint[] o, int idx, int n, int width)
            {
                int end = idx + n, t = idx - width;
                switch (mode)
                {
                    case 0: for (; idx < end; idx++) o[idx] = AddPixels(i[idx], 0xff000000u); break;
                    case 1: for (; idx < end; idx++) o[idx] = AddPixels(i[idx], o[idx - 1]); break;
                    case 2: for (; idx < end; idx++, t++) o[idx] = AddPixels(i[idx], o[t]); break;
                    case 3: for (; idx < end; idx++, t++) o[idx] = AddPixels(i[idx], o[t + 1]); break;
                    case 4: for (; idx < end; idx++, t++) o[idx] = AddPixels(i[idx], o[t - 1]); break;
                    case 5: for (; idx < end; idx++, t++) o[idx] = AddPixels(i[idx], Average2(Average2(o[idx - 1], o[t + 1]), o[t])); break;
                    case 6: for (; idx < end; idx++, t++) o[idx] = AddPixels(i[idx], Average2(o[idx - 1], o[t - 1])); break;
                    case 7: for (; idx < end; idx++, t++) o[idx] = AddPixels(i[idx], Average2(o[idx - 1], o[t])); break;
                    case 8: for (; idx < end; idx++, t++) o[idx] = AddPixels(i[idx], Average2(o[t - 1], o[t])); break;
                    case 9: for (; idx < end; idx++, t++) o[idx] = AddPixels(i[idx], Average2(o[t], o[t + 1])); break;
                    case 10: for (; idx < end; idx++, t++) o[idx] = AddPixels(i[idx], Average2(Average2(o[idx - 1], o[t - 1]), Average2(o[t], o[t + 1]))); break;
                    case 11: for (; idx < end; idx++, t++) o[idx] = AddPixels(i[idx], Select(o[t], o[idx - 1], o[t - 1])); break;
                    case 12: for (; idx < end; idx++, t++) o[idx] = AddPixels(i[idx], ClampedAddSubtractFull(o[idx - 1], o[t], o[t - 1])); break;
                    case 13: for (; idx < end; idx++, t++) o[idx] = AddPixels(i[idx], ClampedAddSubtractHalf(o[idx - 1], o[t], o[t - 1])); break;
                    default: for (; idx < end; idx++) o[idx] = AddPixels(i[idx], 0xff000000u); break;   // 14, 15: black, like the reference
                }
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)] static int ColorDelta(sbyte pred, sbyte color) => (pred * color) >> 5;
            static void ColorSpaceInverse(LTransform t, uint[] i, uint[] o)
            {
                int width = t.XSize, height = t.YSize, bits = t.Bits, tilesPerRow = SubSampleSize(width, bits); var codes = t.Data!;
                for (int y = 0; y < height; y++)
                {
                    int row = y * width, codeRow = (y >> bits) * tilesPerRow;
                    for (int x = 0; x < width; x++)
                    {
                        uint code = codes[codeRow + (x >> bits)];
                        sbyte g2r = (sbyte)(code & 0xff), g2b = (sbyte)((code >> 8) & 0xff), r2b = (sbyte)((code >> 16) & 0xff);
                        uint argb = i[row + x]; sbyte green = (sbyte)(argb >> 8);
                        int red = (int)((argb >> 16) & 0xff), blue = (int)(argb & 0xff);
                        red = (red + ColorDelta(g2r, green)) & 0xff;
                        blue = (blue + ColorDelta(g2b, green) + ColorDelta(r2b, (sbyte)red)) & 0xff;
                        o[row + x] = (argb & 0xff00ff00u) | (uint)red << 16 | (uint)blue;
                    }
                }
            }
            static void ColorIndexInverse(LTransform t, uint[] i, uint[] o)
            {
                int width = t.XSize, height = t.YSize, bits = t.Bits; var map = t.Data!;
                int bitsPerPixel = 8 >> bits, inWidth = SubSampleSize(width, bits);
                if (bits == 0) { for (int k = 0; k < o.Length; k++) o[k] = map[(i[k] >> 8) & 0xff]; return; }
                int perByte = 1 << bits; uint bitMask = (1u << bitsPerPixel) - 1;
                for (int y = 0; y < height; y++)
                {
                    int src = y * inWidth, dst = y * width;
                    for (int x = 0; x < width; x += perByte)
                    {
                        uint packed = (i[src++] >> 8) & 0xff;
                        for (int k = 0; k < perByte && x + k < width; k++) { o[dst + x + k] = map[packed & bitMask]; packed >>= bitsPerPixel; }
                    }
                }
            }
        }

        // =========================================================================================== VP8 (lossy)
        /// <summary>VP8 boolean (arithmetic) decoder - same buffering as libwebp's 56-bit reader.</summary>
        struct BoolDecoder
        {
            readonly byte[] d; int pos; readonly int end; uint range; ulong value; int bits; public bool Eof;
            public BoolDecoder(byte[] data, int off, int len) { d = data; pos = off; end = off + len; range = 254; value = 0; bits = -8; Eof = false; Load(); }
            void Load()
            {
                if (pos + 8 <= end)
                {
                    ulong v = (ulong)d[pos] << 48 | (ulong)d[pos + 1] << 40 | (ulong)d[pos + 2] << 32 | (ulong)d[pos + 3] << 24 | (ulong)d[pos + 4] << 16 | (ulong)d[pos + 5] << 8 | d[pos + 6];
                    pos += 7; value = v | (value << 56); bits += 56;
                }
                else if (pos < end) { bits += 8; value = d[pos++] | (value << 8); }
                else if (!Eof) { value <<= 8; bits += 8; Eof = true; }
                else bits = 0;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int GetBit(int prob)
            {
                uint r = range;
                if (bits < 0) Load();
                int pos = bits;
                uint split = (r * (uint)prob) >> 8;
                uint v = (uint)(value >> pos);
                int bit;
                if (v > split) { r -= split; value -= (ulong)(split + 1) << pos; bit = 1; }
                else { r = split + 1; bit = 0; }
                int shift = 7 ^ (31 - System.Numerics.BitOperations.LeadingZeroCount(r));
                r <<= shift; bits -= shift;
                range = r - 1;
                return bit;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int GetSigned(int v)
            {
                if (bits < 0) Load();
                int pos = bits;
                uint split = range >> 1;
                uint val = (uint)(value >> pos);
                int mask = (int)(split - val) >> 31;
                bits -= 1;
                range += (uint)mask; range |= 1;
                value -= (ulong)((split + 1) & (uint)mask) << pos;
                return (v ^ mask) - mask;
            }
            public uint GetValue(int n) { uint v = 0; while (n-- > 0) v |= (uint)GetBit(0x80) << n; return v; }
            public int GetSignedValue(int n) { int v = (int)GetValue(n); return GetBit(0x80) != 0 ? -v : v; }
            public int Get() => GetBit(0x80);
        }

        sealed class Vp8Decoder
        {
            const int BPS = 32, YOff = BPS + 8, UOff = YOff + BPS * 16 + BPS, VOff = UOff + 16, YuvSize = BPS * 17 + BPS * 9;
            static readonly byte[] Bands = { 0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0 };
            static readonly byte[] Zigzag = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };
            static readonly byte[] Cat3 = { 173, 148, 140 }, Cat4 = { 176, 155, 140, 135 }, Cat5 = { 180, 157, 141, 134, 130 }, Cat6 = { 254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129 };
            static readonly byte[][] Cat3456 = { Cat3, Cat4, Cat5, Cat6 };
            static readonly ushort[] Scan = { 0, 4, 8, 12, 4 * BPS, 4 * BPS + 4, 4 * BPS + 8, 4 * BPS + 12, 8 * BPS, 8 * BPS + 4, 8 * BPS + 8, 8 * BPS + 12, 12 * BPS, 12 * BPS + 4, 12 * BPS + 8, 12 * BPS + 12 };
            static readonly sbyte[] YModesIntra4 = { -0, 1, -1, 2, -2, 3, 4, 6, -3, 5, -4, -5, -6, 7, -7, 8, -8, -9 };
            static readonly byte[] FilterExtraRows = { 0, 2, 8 };

            readonly byte[] d; readonly int off, len;
            int width, height, mbW, mbH;
            // headers
            bool useSegment, updateMap, absoluteDelta; readonly sbyte[] segQuant = new sbyte[4], segFilter = new sbyte[4];
            readonly byte[] segProba = { 255, 255, 255 };
            bool filterSimple; int filterLevel, filterSharpness; bool useLfDelta; readonly int[] refLfDelta = new int[4], modeLfDelta = new int[4];
            int filterType;                                   // 0 none, 1 simple, 2 complex
            readonly int[,] dqY1 = new int[4, 2], dqY2 = new int[4, 2], dqUV = new int[4, 2];
            readonly byte[] proba = new byte[4 * 8 * 3 * 11];
            bool useSkipProba; int skipProba;
            // per-MB state
            byte[] intraT = Array.Empty<byte>(); readonly byte[] intraL = new byte[4];
            byte[] nz = Array.Empty<byte>(), nzDc = Array.Empty<byte>();          // index 0 = left context, x+1 = column x
            readonly short[] coeffs = new short[384]; bool isI4x4; readonly byte[] imodes = new byte[16]; int uvMode, segment; bool skip; uint nonZeroY, nonZeroUV;
            readonly byte[] fLimit = new byte[8], fILevel = new byte[8], fHev = new byte[8], fInner = new byte[8];   // [segment*2 + i4x4]
            byte[] mbFLimit = Array.Empty<byte>(), mbFILevel = Array.Empty<byte>(), mbFHev = Array.Empty<byte>(), mbFInner = Array.Empty<byte>();
            // planes
            byte[] yPlane = Array.Empty<byte>(), uPlane = Array.Empty<byte>(), vPlane = Array.Empty<byte>(); int yStride, uvStride;
            byte[] topY = Array.Empty<byte>(), topU = Array.Empty<byte>(), topV = Array.Empty<byte>();
            readonly byte[] yuv = new byte[YuvSize];
            BoolDecoder br; BoolDecoder[] parts = Array.Empty<BoolDecoder>();

            public Vp8Decoder(byte[] data, int off, int len) { d = data; this.off = off; this.len = len; }

            public int[] Decode(out int w, out int h)
            {
                ParseHeaders();
                w = width; h = height;
                Allocate();
                ParseFrame();
                if (filterType > 0) FilterFrame();
                return ToArgb();
            }

            // ---- headers
            void ParseHeaders()
            {
                int p = off, end = off + len;
                if (len < 10) throw Bad("truncated VP8 header");
                uint bits = (uint)(d[p] | d[p + 1] << 8 | d[p + 2] << 16);
                bool keyFrame = (bits & 1) == 0; int profile = (int)(bits >> 1) & 7; bool show = ((bits >> 4) & 1) != 0; int part0 = (int)(bits >> 5);
                if (profile > 3) throw Bad("bad VP8 profile");
                if (!show) throw Bad("VP8 frame not displayable");
                if (!keyFrame) throw Bad("VP8 inter frame (not a still image)");
                p += 3;
                if (d[p] != 0x9d || d[p + 1] != 0x01 || d[p + 2] != 0x2a) throw Bad("bad VP8 start code");
                width = (d[p + 4] << 8 | d[p + 3]) & 0x3fff; height = (d[p + 6] << 8 | d[p + 5]) & 0x3fff;
                p += 7;
                if (width == 0 || height == 0) throw Bad("zero VP8 size");
                mbW = (width + 15) >> 4; mbH = (height + 15) >> 4;
                if (part0 > end - p) throw Bad("bad VP8 partition length");
                br = new BoolDecoder(d, p, part0);
                p += part0;
                br.Get(); br.Get();                                   // colour space, clamping type
                // segment header
                useSegment = br.Get() != 0;
                if (useSegment)
                {
                    updateMap = br.Get() != 0;
                    if (br.Get() != 0)
                    {
                        absoluteDelta = br.Get() != 0;
                        for (int s = 0; s < 4; s++) segQuant[s] = (sbyte)(br.Get() != 0 ? br.GetSignedValue(7) : 0);
                        for (int s = 0; s < 4; s++) segFilter[s] = (sbyte)(br.Get() != 0 ? br.GetSignedValue(6) : 0);
                    }
                    else absoluteDelta = true;
                    if (updateMap) for (int s = 0; s < 3; s++) segProba[s] = (byte)(br.Get() != 0 ? br.GetValue(8) : 255);
                }
                else { updateMap = false; absoluteDelta = true; }
                if (br.Eof) throw Bad("cannot parse segment header");
                // filter header
                filterSimple = br.Get() != 0; filterLevel = (int)br.GetValue(6); filterSharpness = (int)br.GetValue(3);
                useLfDelta = br.Get() != 0;
                if (useLfDelta && br.Get() != 0)
                {
                    for (int i = 0; i < 4; i++) if (br.Get() != 0) refLfDelta[i] = br.GetSignedValue(6);
                    for (int i = 0; i < 4; i++) if (br.Get() != 0) modeLfDelta[i] = br.GetSignedValue(6);
                }
                filterType = filterLevel == 0 ? 0 : filterSimple ? 1 : 2;
                if (br.Eof) throw Bad("cannot parse filter header");
                // token partitions
                int numParts = 1 << (int)br.GetValue(2);
                int last = numParts - 1; int size = end - p;
                if (size < 3 * last) throw Bad("truncated partition table");
                parts = new BoolDecoder[numParts];
                int partStart = p + last * 3, sizeLeft = size - last * 3, sz = p;
                for (int i = 0; i < last; i++)
                {
                    int psize = d[sz] | d[sz + 1] << 8 | d[sz + 2] << 16; if (psize > sizeLeft) psize = sizeLeft;
                    parts[i] = new BoolDecoder(d, partStart, psize); partStart += psize; sizeLeft -= psize; sz += 3;
                }
                parts[last] = new BoolDecoder(d, partStart, sizeLeft);
                if (partStart >= end) throw Bad("truncated partitions");
                ParseQuant();
                br.Get();                                             // update_proba (ignored for key frames)
                ParseProba();
            }

            void ParseQuant()
            {
                int baseQ0 = (int)br.GetValue(7);
                int dqy1dc = br.Get() != 0 ? br.GetSignedValue(4) : 0, dqy2dc = br.Get() != 0 ? br.GetSignedValue(4) : 0, dqy2ac = br.Get() != 0 ? br.GetSignedValue(4) : 0;
                int dquvdc = br.Get() != 0 ? br.GetSignedValue(4) : 0, dquvac = br.Get() != 0 ? br.GetSignedValue(4) : 0;
                for (int i = 0; i < 4; i++)
                {
                    int q;
                    if (useSegment) { q = segQuant[i]; if (!absoluteDelta) q += baseQ0; }
                    else { if (i > 0) { dqY1[i, 0] = dqY1[0, 0]; dqY1[i, 1] = dqY1[0, 1]; dqY2[i, 0] = dqY2[0, 0]; dqY2[i, 1] = dqY2[0, 1]; dqUV[i, 0] = dqUV[0, 0]; dqUV[i, 1] = dqUV[0, 1]; continue; } q = baseQ0; }
                    dqY1[i, 0] = DcTable[Clip(q + dqy1dc, 127)]; dqY1[i, 1] = AcTable[Clip(q, 127)];
                    dqY2[i, 0] = DcTable[Clip(q + dqy2dc, 127)] * 2;
                    int y2ac = (AcTable[Clip(q + dqy2ac, 127)] * 101581) >> 16; dqY2[i, 1] = y2ac < 8 ? 8 : y2ac;
                    dqUV[i, 0] = DcTable[Clip(q + dquvdc, 117)]; dqUV[i, 1] = AcTable[Clip(q + dquvac, 127)];
                }
            }
            static int Clip(int v, int m) => v < 0 ? 0 : v > m ? m : v;

            void ParseProba()
            {
                for (int i = 0; i < proba.Length; i++) proba[i] = br.GetBit(CoeffsUpdateProba[i]) != 0 ? (byte)br.GetValue(8) : CoeffsProba0[i];
                useSkipProba = br.Get() != 0;
                if (useSkipProba) skipProba = (int)br.GetValue(8);
            }

            void Allocate()
            {
                yStride = mbW * 16; uvStride = mbW * 8;
                yPlane = new byte[yStride * mbH * 16]; uPlane = new byte[uvStride * mbH * 8]; vPlane = new byte[uvStride * mbH * 8];
                intraT = new byte[4 * mbW]; nz = new byte[mbW + 1]; nzDc = new byte[mbW + 1];
                topY = new byte[16 * mbW]; topU = new byte[8 * mbW]; topV = new byte[8 * mbW];
                if (filterType > 0)
                {
                    mbFLimit = new byte[mbW * mbH]; mbFILevel = new byte[mbW * mbH]; mbFHev = new byte[mbW * mbH]; mbFInner = new byte[mbW * mbH];
                    for (int s = 0; s < 4; s++)
                    {
                        int baseLevel = useSegment ? (absoluteDelta ? segFilter[s] : segFilter[s] + filterLevel) : filterLevel;
                        for (int i4 = 0; i4 <= 1; i4++)
                        {
                            int level = baseLevel;
                            if (useLfDelta) { level += refLfDelta[0]; if (i4 != 0) level += modeLfDelta[0]; }
                            level = level < 0 ? 0 : level > 63 ? 63 : level;
                            int k = s * 2 + i4;
                            if (level > 0)
                            {
                                int ilevel = level;
                                if (filterSharpness > 0) { ilevel >>= filterSharpness > 4 ? 2 : 1; if (ilevel > 9 - filterSharpness) ilevel = 9 - filterSharpness; }
                                if (ilevel < 1) ilevel = 1;
                                fILevel[k] = (byte)ilevel; fLimit[k] = (byte)(2 * level + ilevel); fHev[k] = (byte)(level >= 40 ? 2 : level >= 15 ? 1 : 0);
                            }
                            else fLimit[k] = 0;
                            fInner[k] = (byte)i4;
                        }
                    }
                }
            }

            // ---- frame
            void ParseFrame()
            {
                int numPartsMask = parts.Length - 1;
                for (int mbY = 0; mbY < mbH; mbY++)
                {
                    ref BoolDecoder tokenBr = ref parts[mbY & numPartsMask];
                    curMbY = mbY;
                    // left contexts for the row
                    nz[0] = 0; nzDc[0] = 0; Array.Clear(intraL, 0, 4);
                    for (int mbX = 0; mbX < mbW; mbX++)
                    {
                        ParseIntraMode(mbX);
                        if (br.Eof) throw Bad("premature end of partition 0");
                        DecodeMB(mbX, ref tokenBr);
                        if (tokenBr.Eof) throw Bad("premature end of VP8 data");
                        Reconstruct(mbX, mbY);
                    }
                }
            }

            void ParseIntraMode(int mbX)
            {
                int top = 4 * mbX;
                segment = 0;
                if (updateMap) segment = br.GetBit(segProba[0]) == 0 ? br.GetBit(segProba[1]) : br.GetBit(segProba[2]) + 2;
                skip = useSkipProba && br.GetBit(skipProba) != 0;
                isI4x4 = br.GetBit(145) == 0;
                if (!isI4x4)
                {
                    int ymode = br.GetBit(156) != 0 ? (br.GetBit(128) != 0 ? 1 /*TM*/ : 3 /*H*/) : (br.GetBit(163) != 0 ? 2 /*V*/ : 0 /*DC*/);
                    imodes[0] = (byte)ymode;
                    for (int i = 0; i < 4; i++) { intraT[top + i] = (byte)ymode; intraL[i] = (byte)ymode; }
                }
                else
                {
                    int m = 0;
                    for (int y = 0; y < 4; y++)
                    {
                        int ymode = intraL[y];
                        for (int x = 0; x < 4; x++)
                        {
                            int prob = (intraT[top + x] * 10 + ymode) * 9;
                            int i = YModesIntra4[br.GetBit(BModesProba[prob])];
                            while (i > 0) i = YModesIntra4[2 * i + br.GetBit(BModesProba[prob + i])];
                            ymode = -i;
                            intraT[top + x] = (byte)ymode;
                            imodes[m++] = (byte)ymode;
                        }
                        intraL[y] = (byte)ymode;
                    }
                }
                uvMode = br.GetBit(142) == 0 ? 0 : br.GetBit(114) == 0 ? 2 : br.GetBit(183) != 0 ? 1 : 3;
            }

            // ---- residuals
            /// <summary>Reads the coefficients of one 4x4 block; returns the index after the last non-zero one (0 = empty).</summary>
            unsafe int GetCoeffs(ref BoolDecoder brRef, int type, int ctx, int dq0, int dq1, int n, int outIdx)
            {
                BoolDecoder br = brRef;                             // local copy: the JIT keeps range / value / bits in registers
                int r = GetCoeffsCore(ref br, type, ctx, dq0, dq1, n, outIdx);
                brRef = br;
                return r;
            }
            unsafe int GetCoeffsCore(ref BoolDecoder br, int type, int ctx, int dq0, int dq1, int n, int outIdx)
            {
                fixed (byte* pr = proba) fixed (short* co = coeffs) fixed (byte* bands = Bands) fixed (byte* zz = Zigzag)
                {
                    short* o = co + outIdx;
                    byte* p = pr + ((type * 8 + bands[n]) * 3 + ctx) * 11;
                    byte* tp = pr + type * 8 * 33;                      // band 0 of this type; band b at tp + b*33
                    for (; n < 16; ++n)
                    {
                        if (br.GetBit(p[0]) == 0) return n;
                        while (br.GetBit(p[1]) == 0)
                        {
                            p = tp + bands[++n] * 33;
                            if (n == 16) return 16;
                        }
                        byte* pctx = tp + bands[n + 1] * 33;
                        int v;
                        if (br.GetBit(p[2]) == 0) { v = 1; p = pctx + 11; }
                        else { v = GetLargeValue(ref br, p); p = pctx + 22; }
                        o[zz[n]] = (short)(br.GetSigned(v) * (n > 0 ? dq1 : dq0));
                    }
                    return 16;
                }
            }
            static unsafe int GetLargeValue(ref BoolDecoder br, byte* p)
            {
                int v;
                if (br.GetBit(p[3]) == 0)
                {
                    if (br.GetBit(p[4]) == 0) v = 2; else v = 3 + br.GetBit(p[5]);
                }
                else
                {
                    if (br.GetBit(p[6]) == 0)
                    {
                        if (br.GetBit(p[7]) == 0) v = 5 + br.GetBit(159);
                        else { v = 7 + 2 * br.GetBit(165); v += br.GetBit(145); }
                    }
                    else
                    {
                        int bit1 = br.GetBit(p[8]); int bit0 = br.GetBit(p[9 + bit1]); int cat = 2 * bit1 + bit0;
                        v = 0; foreach (byte t in Cat3456[cat]) v += v + br.GetBit(t);
                        v += 3 + (8 << cat);
                    }
                }
                return v;
            }
            static uint NzCodeBits(uint nzCoeffs, int nzc, int dcNz) { nzCoeffs <<= 2; nzCoeffs |= (uint)(nzc > 3 ? 3 : nzc > 1 ? 2 : dcNz); return nzCoeffs; }

            bool ParseResiduals(int mbX, ref BoolDecoder tokenBr)
            {
                int seg = segment;
                Array.Clear(coeffs, 0, 384);
                int dst = 0, first, acType;
                int mbNz = nz[mbX + 1], leftNz = nz[0];
                if (!isI4x4)
                {
                    int ctx = nzDc[mbX + 1] + nzDc[0];
                    int nzc = GetCoeffsDc(ref tokenBr, ctx, dqY2[seg, 0], dqY2[seg, 1]);
                    nzDc[mbX + 1] = nzDc[0] = (byte)(nzc > 0 ? 1 : 0);
                    if (nzc > 1) TransformWHT(dcScratch, coeffs);
                    else { int dc0 = (dcScratch[0] + 3) >> 3; for (int i = 0; i < 256; i += 16) coeffs[i] = (short)dc0; }
                    first = 1; acType = 0;
                }
                else { first = 0; acType = 3; }
                int tnz = mbNz & 0x0f, lnz = leftNz & 0x0f;
                uint nonZeroY = 0;
                for (int y = 0; y < 4; y++)
                {
                    int l = lnz & 1; uint nzCoeffs = 0;
                    for (int x = 0; x < 4; x++)
                    {
                        int ctx = l + (tnz & 1);
                        int nzc = GetCoeffs(ref tokenBr, acType, ctx, dqY1[seg, 0], dqY1[seg, 1], first, dst);
                        l = nzc > first ? 1 : 0;
                        tnz = (tnz >> 1) | (l << 7);
                        nzCoeffs = NzCodeBits(nzCoeffs, nzc, coeffs[dst] != 0 ? 1 : 0);
                        dst += 16;
                    }
                    tnz >>= 4; lnz = (lnz >> 1) | (l << 7);
                    nonZeroY = (nonZeroY << 8) | nzCoeffs;
                }
                int outTNz = tnz, outLNz = lnz >> 4;
                uint nonZeroUV = 0;
                for (int ch = 0; ch < 4; ch += 2)
                {
                    uint nzCoeffs = 0;
                    tnz = mbNz >> (4 + ch); lnz = leftNz >> (4 + ch);
                    for (int y = 0; y < 2; y++)
                    {
                        int l = lnz & 1;
                        for (int x = 0; x < 2; x++)
                        {
                            int ctx = l + (tnz & 1);
                            int nzc = GetCoeffs(ref tokenBr, 2, ctx, dqUV[seg, 0], dqUV[seg, 1], 0, dst);
                            l = nzc > 0 ? 1 : 0;
                            tnz = (tnz >> 1) | (l << 3);
                            nzCoeffs = NzCodeBits(nzCoeffs, nzc, coeffs[dst] != 0 ? 1 : 0);
                            dst += 16;
                        }
                        tnz >>= 2; lnz = (lnz >> 1) | (l << 5);
                    }
                    nonZeroUV |= nzCoeffs << (4 * ch);
                    outTNz |= (tnz << 4) << ch; outLNz |= (lnz & 0xf0) << ch;
                }
                nz[mbX + 1] = (byte)outTNz; nz[0] = (byte)outLNz;
                this.nonZeroY = nonZeroY; this.nonZeroUV = nonZeroUV;
                return (nonZeroY | nonZeroUV) == 0;
            }
            // DC (Y2) block: same reader, writing into the 16-entry scratch
            readonly short[] dcScratch = new short[16];
            unsafe int GetCoeffsDc(ref BoolDecoder br, int ctx, int dq0, int dq1)
            {
                Array.Clear(dcScratch, 0, 16);
                fixed (byte* pr = proba) fixed (short* o = dcScratch) fixed (byte* bands = Bands) fixed (byte* zz = Zigzag)
                {
                    const int type = 1; int n = 0;
                    byte* tp = pr + type * 8 * 33;
                    byte* p = pr + ((type * 8 + bands[n]) * 3 + ctx) * 11;
                    for (; n < 16; ++n)
                    {
                        if (br.GetBit(p[0]) == 0) return n;
                        while (br.GetBit(p[1]) == 0)
                        {
                            p = tp + bands[++n] * 33;
                            if (n == 16) return 16;
                        }
                        byte* pctx = tp + bands[n + 1] * 33;
                        int v;
                        if (br.GetBit(p[2]) == 0) { v = 1; p = pctx + 11; }
                        else { v = GetLargeValue(ref br, p); p = pctx + 22; }
                        o[zz[n]] = (short)(br.GetSigned(v) * (n > 0 ? dq1 : dq0));
                    }
                    return 16;
                }
            }

            void DecodeMB(int mbX, ref BoolDecoder tokenBr)
            {
                bool sk = useSkipProba && skip;
                if (!sk) sk = ParseResiduals(mbX, ref tokenBr);
                else
                {
                    nz[0] = nz[mbX + 1] = 0;
                    if (!isI4x4) nzDc[0] = nzDc[mbX + 1] = 0;
                    nonZeroY = 0; nonZeroUV = 0;
                }
                if (filterType > 0)
                {
                    int k = segment * 2 + (isI4x4 ? 1 : 0); int i = curMbIndex(mbX);
                    mbFLimit[i] = fLimit[k]; mbFILevel[i] = fILevel[k]; mbFHev[i] = fHev[k]; mbFInner[i] = (byte)(fInner[k] | (sk ? 0 : 1));
                }
            }
            int curMbY; int curMbIndex(int mbX) => curMbY * mbW + mbX;

            // ---- transforms (dst has stride BPS inside yuv[])
            static byte Clip8(int v) => (v & ~0xff) == 0 ? (byte)v : v < 0 ? (byte)0 : (byte)255;
            [MethodImpl(MethodImplOptions.AggressiveInlining)] static int Mul1(int a) => ((a * 20091) >> 16) + a;
            [MethodImpl(MethodImplOptions.AggressiveInlining)] static int Mul2(int a) => (a * 35468) >> 16;
            void TransformOne(int inp, int dst)
            {
                Span<int> C = stackalloc int[16]; var input = coeffs; var o = yuv;
                for (int i = 0; i < 4; i++)
                {
                    int a = input[inp + i] + input[inp + 8 + i], b = input[inp + i] - input[inp + 8 + i];
                    int c = Mul2(input[inp + 4 + i]) - Mul1(input[inp + 12 + i]), dd = Mul1(input[inp + 4 + i]) + Mul2(input[inp + 12 + i]);
                    C[i * 4] = a + dd; C[i * 4 + 1] = b + c; C[i * 4 + 2] = b - c; C[i * 4 + 3] = a - dd;
                }
                for (int i = 0; i < 4; i++)
                {
                    int dc = C[i] + 4, a = dc + C[8 + i], b = dc - C[8 + i];
                    int c = Mul2(C[4 + i]) - Mul1(C[12 + i]), dd = Mul1(C[4 + i]) + Mul2(C[12 + i]);
                    int r = dst + i * BPS;
                    o[r] = Clip8(o[r] + ((a + dd) >> 3)); o[r + 1] = Clip8(o[r + 1] + ((b + c) >> 3)); o[r + 2] = Clip8(o[r + 2] + ((b - c) >> 3)); o[r + 3] = Clip8(o[r + 3] + ((a - dd) >> 3));
                }
            }
            void TransformAC3(int inp, int dst)
            {
                var input = coeffs; var o = yuv;
                int a = input[inp] + 4, c4 = Mul2(input[inp + 4]), d4 = Mul1(input[inp + 4]), c1 = Mul2(input[inp + 1]), d1 = Mul1(input[inp + 1]);
                Store2(o, dst, a + d4, d1, c1); Store2(o, dst + BPS, a + c4, d1, c1); Store2(o, dst + 2 * BPS, a - c4, d1, c1); Store2(o, dst + 3 * BPS, a - d4, d1, c1);
            }
            static void Store2(byte[] o, int r, int dc, int dd, int c)
            {
                o[r] = Clip8(o[r] + ((dc + dd) >> 3)); o[r + 1] = Clip8(o[r + 1] + ((dc + c) >> 3)); o[r + 2] = Clip8(o[r + 2] + ((dc - c) >> 3)); o[r + 3] = Clip8(o[r + 3] + ((dc - dd) >> 3));
            }
            void TransformDC(int inp, int dst)
            {
                int dc = coeffs[inp] + 4; var o = yuv;
                for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) { int k = dst + i + j * BPS; o[k] = Clip8(o[k] + (dc >> 3)); }
            }
            void DoTransform(uint bits, int src, int dst)
            {
                switch (bits >> 30) { case 3: TransformOne(src, dst); break; case 2: TransformAC3(src, dst); break; case 1: TransformDC(src, dst); break; }
            }
            void DoUVTransform(uint bits, int src, int dst)
            {
                if ((bits & 0xff) != 0)
                {
                    if ((bits & 0xaa) != 0) { TransformOne(src, dst); TransformOne(src + 16, dst + 4); TransformOne(src + 32, dst + 4 * BPS); TransformOne(src + 48, dst + 4 * BPS + 4); }
                    else
                    {
                        if (coeffs[src] != 0) TransformDC(src, dst); if (coeffs[src + 16] != 0) TransformDC(src + 16, dst + 4);
                        if (coeffs[src + 32] != 0) TransformDC(src + 32, dst + 4 * BPS); if (coeffs[src + 48] != 0) TransformDC(src + 48, dst + 4 * BPS + 4);
                    }
                }
            }
            static void TransformWHT(Span<short> inp, short[] outp)
            {
                Span<int> tmp = stackalloc int[16];
                for (int i = 0; i < 4; i++)
                {
                    int a0 = inp[i] + inp[12 + i], a1 = inp[4 + i] + inp[8 + i], a2 = inp[4 + i] - inp[8 + i], a3 = inp[i] - inp[12 + i];
                    tmp[i] = a0 + a1; tmp[8 + i] = a0 - a1; tmp[4 + i] = a3 + a2; tmp[12 + i] = a3 - a2;
                }
                int o = 0;
                for (int i = 0; i < 4; i++)
                {
                    int dc = tmp[i * 4] + 3, a0 = dc + tmp[3 + i * 4], a1 = tmp[1 + i * 4] + tmp[2 + i * 4], a2 = tmp[1 + i * 4] - tmp[2 + i * 4], a3 = dc - tmp[3 + i * 4];
                    outp[o] = (short)((a0 + a1) >> 3); outp[o + 16] = (short)((a3 + a2) >> 3); outp[o + 32] = (short)((a0 - a1) >> 3); outp[o + 48] = (short)((a3 - a2) >> 3);
                    o += 64;
                }
            }

            // ---- intra predictors (dst = index into yuv, stride BPS; the borders at -1 are valid)
            static byte Clip255(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
            static void TrueMotion(byte[] o, int dst, int size)
            {
                int top = dst - BPS, tl = o[top - 1];
                for (int y = 0; y < size; y++) { int left = o[dst - 1] - tl; for (int x = 0; x < size; x++) o[dst + x] = Clip255(o[top + x] + left); dst += BPS; }
            }
            static void Fill(byte[] o, int dst, int size, int v) { for (int j = 0; j < size; j++) for (int i = 0; i < size; i++) o[dst + j * BPS + i] = (byte)v; }
            static void PredBig(byte[] o, int dst, int size, int mode)
            {   // 16x16 luma / 8x8 chroma: 0 DC, 1 TM, 2 VE, 3 HE, 4 DC no top, 5 DC no left, 6 DC nothing
                int shift = size == 16 ? 5 : 4;
                switch (mode)
                {
                    case 0: { int dc = 1 << (shift - 1); for (int j = 0; j < size; j++) dc += o[dst - 1 + j * BPS] + o[dst + j - BPS]; Fill(o, dst, size, dc >> shift); break; }
                    case 1: TrueMotion(o, dst, size); break;
                    case 2: for (int j = 0; j < size; j++) Buffer.BlockCopy(o, dst - BPS, o, dst + j * BPS, size); break;
                    case 3: for (int j = 0; j < size; j++) { byte v = o[dst - 1 + j * BPS]; for (int i = 0; i < size; i++) o[dst + j * BPS + i] = v; } break;
                    case 4: { int dc = 1 << (shift - 2); for (int j = 0; j < size; j++) dc += o[dst - 1 + j * BPS]; Fill(o, dst, size, dc >> (shift - 1)); break; }
                    case 5: { int dc = 1 << (shift - 2); for (int i = 0; i < size; i++) dc += o[dst + i - BPS]; Fill(o, dst, size, dc >> (shift - 1)); break; }
                    default: Fill(o, dst, size, 0x80); break;
                }
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)] static byte Avg3(int a, int b, int c) => (byte)((a + 2 * b + c + 2) >> 2);
            [MethodImpl(MethodImplOptions.AggressiveInlining)] static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);
            static void Pred4(byte[] o, int dst, int mode)
            {
                int top = dst - BPS;
                int A = o[top], B = o[top + 1], C = o[top + 2], D = o[top + 3], E = o[top + 4], F = o[top + 5], G = o[top + 6], H = o[top + 7];
                int X = o[top - 1], I = o[dst - 1], J = o[dst - 1 + BPS], K = o[dst - 1 + 2 * BPS], L = o[dst - 1 + 3 * BPS];
                void S(int x, int y, byte v) => o[dst + x + y * BPS] = v;
                switch (mode)
                {
                    case 0: { int dc = 4; for (int i = 0; i < 4; i++) dc += o[top + i] + o[dst - 1 + i * BPS]; dc >>= 3; for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) o[dst + j * BPS + i] = (byte)dc; break; }
                    case 1: TrueMotion(o, dst, 4); break;
                    case 2: { byte v0 = Avg3(X, A, B), v1 = Avg3(A, B, C), v2 = Avg3(B, C, D), v3 = Avg3(C, D, E); for (int j = 0; j < 4; j++) { S(0, j, v0); S(1, j, v1); S(2, j, v2); S(3, j, v3); } break; }
                    case 3: { byte r0 = Avg3(X, I, J), r1 = Avg3(I, J, K), r2 = Avg3(J, K, L), r3 = Avg3(K, L, L); for (int i = 0; i < 4; i++) { S(i, 0, r0); S(i, 1, r1); S(i, 2, r2); S(i, 3, r3); } break; }
                    case 4:   // RD
                        S(0, 3, Avg3(J, K, L)); byte a = Avg3(I, J, K); S(1, 3, a); S(0, 2, a); a = Avg3(X, I, J); S(2, 3, a); S(1, 2, a); S(0, 1, a);
                        a = Avg3(A, X, I); S(3, 3, a); S(2, 2, a); S(1, 1, a); S(0, 0, a); a = Avg3(B, A, X); S(3, 2, a); S(2, 1, a); S(1, 0, a); a = Avg3(C, B, A); S(3, 1, a); S(2, 0, a); S(3, 0, Avg3(D, C, B)); break;
                    case 5:   // VR
                        a = Avg2(X, A); S(0, 0, a); S(1, 2, a); a = Avg2(A, B); S(1, 0, a); S(2, 2, a); a = Avg2(B, C); S(2, 0, a); S(3, 2, a); S(3, 0, Avg2(C, D));
                        S(0, 3, Avg3(K, J, I)); S(0, 2, Avg3(J, I, X)); a = Avg3(I, X, A); S(0, 1, a); S(1, 3, a); a = Avg3(X, A, B); S(1, 1, a); S(2, 3, a); a = Avg3(A, B, C); S(2, 1, a); S(3, 3, a); S(3, 1, Avg3(B, C, D)); break;
                    case 6:   // LD
                        S(0, 0, Avg3(A, B, C)); a = Avg3(B, C, D); S(1, 0, a); S(0, 1, a); a = Avg3(C, D, E); S(2, 0, a); S(1, 1, a); S(0, 2, a); a = Avg3(D, E, F); S(3, 0, a); S(2, 1, a); S(1, 2, a); S(0, 3, a);
                        a = Avg3(E, F, G); S(3, 1, a); S(2, 2, a); S(1, 3, a); a = Avg3(F, G, H); S(3, 2, a); S(2, 3, a); S(3, 3, Avg3(G, H, H)); break;
                    case 7:   // VL
                        S(0, 0, Avg2(A, B)); a = Avg2(B, C); S(1, 0, a); S(0, 2, a); a = Avg2(C, D); S(2, 0, a); S(1, 2, a); a = Avg2(D, E); S(3, 0, a); S(2, 2, a);
                        S(0, 1, Avg3(A, B, C)); a = Avg3(B, C, D); S(1, 1, a); S(0, 3, a); a = Avg3(C, D, E); S(2, 1, a); S(1, 3, a); a = Avg3(D, E, F); S(3, 1, a); S(2, 3, a); S(3, 2, Avg3(E, F, G)); S(3, 3, Avg3(F, G, H)); break;
                    case 8:   // HD
                        a = Avg2(I, X); S(0, 0, a); S(2, 1, a); a = Avg2(J, I); S(0, 1, a); S(2, 2, a); a = Avg2(K, J); S(0, 2, a); S(2, 3, a); S(0, 3, Avg2(L, K));
                        S(3, 0, Avg3(A, B, C)); S(2, 0, Avg3(X, A, B)); a = Avg3(I, X, A); S(1, 0, a); S(3, 1, a); a = Avg3(J, I, X); S(1, 1, a); S(3, 2, a); a = Avg3(K, J, I); S(1, 2, a); S(3, 3, a); S(1, 3, Avg3(L, K, J)); break;
                    default:  // HU
                        S(0, 0, Avg2(I, J)); a = Avg2(J, K); S(2, 0, a); S(0, 1, a); a = Avg2(K, L); S(2, 1, a); S(0, 2, a); S(1, 0, Avg3(I, J, K)); a = Avg3(J, K, L); S(3, 0, a); S(1, 1, a); a = Avg3(K, L, L); S(3, 1, a); S(1, 2, a);
                        S(3, 2, (byte)L); S(2, 2, (byte)L); S(0, 3, (byte)L); S(1, 3, (byte)L); S(2, 3, (byte)L); S(3, 3, (byte)L); break;
                }
            }
            static int CheckMode(int mbX, int mbY, int mode) => mode == 0 ? (mbX == 0 ? (mbY == 0 ? 6 : 5) : (mbY == 0 ? 4 : 0)) : mode;

            // ---- reconstruction of one macroblock into the work buffer, then into the planes (unfiltered)
            void Reconstruct(int mbX, int mbY)
            {
                curMbY = mbY;
                var o = yuv;
                if (mbX == 0)
                {   // row start: left borders
                    for (int j = 0; j < 16; j++) o[YOff + j * BPS - 1] = 129;
                    for (int j = 0; j < 8; j++) { o[UOff + j * BPS - 1] = 129; o[VOff + j * BPS - 1] = 129; }
                    if (mbY > 0) { o[YOff - 1 - BPS] = 129; o[UOff - 1 - BPS] = 129; o[VOff - 1 - BPS] = 129; }
                    else
                    {
                        for (int i = 0; i < 21; i++) o[YOff - BPS - 1 + i] = 127;
                        for (int i = 0; i < 9; i++) { o[UOff - BPS - 1 + i] = 127; o[VOff - BPS - 1 + i] = 127; }
                    }
                }
                else
                {   // shift the previous block's right columns into the left border (rows -1..15 / -1..7)
                    for (int j = -1; j < 16; j++) { int r = YOff + j * BPS; o[r - 4] = o[r + 12]; o[r - 3] = o[r + 13]; o[r - 2] = o[r + 14]; o[r - 1] = o[r + 15]; }
                    for (int j = -1; j < 8; j++) { int r = UOff + j * BPS; o[r - 4] = o[r + 4]; o[r - 3] = o[r + 5]; o[r - 2] = o[r + 6]; o[r - 1] = o[r + 7]; r = VOff + j * BPS; o[r - 4] = o[r + 4]; o[r - 3] = o[r + 5]; o[r - 2] = o[r + 6]; o[r - 1] = o[r + 7]; }
                }
                if (mbY > 0)
                {
                    Buffer.BlockCopy(topY, mbX * 16, o, YOff - BPS, 16); Buffer.BlockCopy(topU, mbX * 8, o, UOff - BPS, 8); Buffer.BlockCopy(topV, mbX * 8, o, VOff - BPS, 8);
                }
                uint bits = nonZeroY;
                if (isI4x4)
                {
                    int tr = YOff - BPS + 16;
                    if (mbY > 0)
                    {
                        if (mbX >= mbW - 1) { byte v = topY[mbX * 16 + 15]; o[tr] = o[tr + 1] = o[tr + 2] = o[tr + 3] = v; }
                        else Buffer.BlockCopy(topY, (mbX + 1) * 16, o, tr, 4);
                    }
                    for (int k = 1; k <= 3; k++) { int r = tr + 4 * k * BPS; o[r] = o[tr]; o[r + 1] = o[tr + 1]; o[r + 2] = o[tr + 2]; o[r + 3] = o[tr + 3]; }
                    for (int n = 0; n < 16; n++, bits <<= 2)
                    {
                        int dst = YOff + Scan[n];
                        Pred4(o, dst, imodes[n]);
                        DoTransform(bits, n * 16, dst);
                    }
                }
                else
                {
                    PredBig(o, YOff, 16, CheckMode(mbX, mbY, imodes[0]));
                    if (bits != 0) for (int n = 0; n < 16; n++, bits <<= 2) DoTransform(bits, n * 16, YOff + Scan[n]);
                }
                {
                    uint bitsUV = nonZeroUV; int pf = CheckMode(mbX, mbY, uvMode);
                    PredBig(o, UOff, 8, pf); PredBig(o, VOff, 8, pf);
                    DoUVTransform(bitsUV, 16 * 16, UOff); DoUVTransform(bitsUV >> 8, 20 * 16, VOff);
                }
                if (mbY < mbH - 1)
                {
                    Buffer.BlockCopy(o, YOff + 15 * BPS, topY, mbX * 16, 16); Buffer.BlockCopy(o, UOff + 7 * BPS, topU, mbX * 8, 8); Buffer.BlockCopy(o, VOff + 7 * BPS, topV, mbX * 8, 8);
                }
                // to the planes
                int yo = mbY * 16 * yStride + mbX * 16, uvo = mbY * 8 * uvStride + mbX * 8;
                for (int j = 0; j < 16; j++) Buffer.BlockCopy(o, YOff + j * BPS, yPlane, yo + j * yStride, 16);
                for (int j = 0; j < 8; j++) { Buffer.BlockCopy(o, UOff + j * BPS, uPlane, uvo + j * uvStride, 8); Buffer.BlockCopy(o, VOff + j * BPS, vPlane, uvo + j * uvStride, 8); }
            }

            // ---- loop filter (whole frame, macroblock raster order = libwebp's row-delayed order)
            [MethodImpl(MethodImplOptions.AggressiveInlining)] static int SClip1(int v) => v < -1020 ? -128 : v > 1020 ? 127 : v < -128 ? -128 : v > 127 ? 127 : v;   // VP8ksclip1: [-1020,1020] -> [-128,127]
            [MethodImpl(MethodImplOptions.AggressiveInlining)] static int SClip2(int v) => v < -16 ? -16 : v > 15 ? 15 : v;                 // VP8ksclip2: [-112,112] -> [-16,15]
            [MethodImpl(MethodImplOptions.AggressiveInlining)] static byte Clip1(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
            static unsafe void DoFilter2(byte* p, int step)
            {
                int p1 = p[-2 * step], p0 = p[-step], q0 = p[0], q1 = p[step];
                int a = 3 * (q0 - p0) + SClip1(p1 - q1); int a1 = SClip2((a + 4) >> 3), a2 = SClip2((a + 3) >> 3);
                p[-step] = Clip1(p0 + a2); p[0] = Clip1(q0 - a1);
            }
            static unsafe void DoFilter4(byte* p, int step)
            {
                int p1 = p[-2 * step], p0 = p[-step], q0 = p[0], q1 = p[step];
                int a = 3 * (q0 - p0); int a1 = SClip2((a + 4) >> 3), a2 = SClip2((a + 3) >> 3), a3 = (a1 + 1) >> 1;
                p[-2 * step] = Clip1(p1 + a3); p[-step] = Clip1(p0 + a2); p[0] = Clip1(q0 - a1); p[step] = Clip1(q1 - a3);
            }
            static unsafe void DoFilter6(byte* p, int step)
            {
                int p2 = p[-3 * step], p1 = p[-2 * step], p0 = p[-step], q0 = p[0], q1 = p[step], q2 = p[2 * step];
                int a = SClip1(3 * (q0 - p0) + SClip1(p1 - q1));
                int a1 = (27 * a + 63) >> 7, a2 = (18 * a + 63) >> 7, a3 = (9 * a + 63) >> 7;
                p[-3 * step] = Clip1(p2 + a3); p[-2 * step] = Clip1(p1 + a2); p[-step] = Clip1(p0 + a1); p[0] = Clip1(q0 - a1); p[step] = Clip1(q1 - a2); p[2 * step] = Clip1(q2 - a3);
            }
            static unsafe bool Hev(byte* p, int step, int thresh) => Math.Abs(p[-2 * step] - p[-step]) > thresh || Math.Abs(p[step] - p[0]) > thresh;
            static unsafe bool NeedsFilter(byte* p, int step, int t) => 4 * Math.Abs(p[-step] - p[0]) + Math.Abs(p[-2 * step] - p[step]) <= t;
            static unsafe bool NeedsFilter2(byte* p, int step, int t, int it)
            {
                int p3 = p[-4 * step], p2 = p[-3 * step], p1 = p[-2 * step], p0 = p[-step], q0 = p[0], q1 = p[step], q2 = p[2 * step], q3 = p[3 * step];
                if (4 * Math.Abs(p0 - q0) + Math.Abs(p1 - q1) > t) return false;
                return Math.Abs(p3 - p2) <= it && Math.Abs(p2 - p1) <= it && Math.Abs(p1 - p0) <= it && Math.Abs(q3 - q2) <= it && Math.Abs(q2 - q1) <= it && Math.Abs(q1 - q0) <= it;
            }
            static unsafe void SimpleVFilter16(byte* p, int stride, int thresh) { int t2 = 2 * thresh + 1; for (int k = 0; k < 16; k++) if (NeedsFilter(p + k, stride, t2)) DoFilter2(p + k, stride); }
            static unsafe void SimpleHFilter16(byte* p, int stride, int thresh) { int t2 = 2 * thresh + 1; for (int k = 0; k < 16; k++) if (NeedsFilter(p + k * stride, 1, t2)) DoFilter2(p + k * stride, 1); }
            static unsafe void FilterLoop26(byte* p, int hstride, int vstride, int size, int thresh, int ithresh, int hevT)
            {
                int t2 = 2 * thresh + 1;
                while (size-- > 0) { if (NeedsFilter2(p, hstride, t2, ithresh)) { if (Hev(p, hstride, hevT)) DoFilter2(p, hstride); else DoFilter6(p, hstride); } p += vstride; }
            }
            static unsafe void FilterLoop24(byte* p, int hstride, int vstride, int size, int thresh, int ithresh, int hevT)
            {
                int t2 = 2 * thresh + 1;
                while (size-- > 0) { if (NeedsFilter2(p, hstride, t2, ithresh)) { if (Hev(p, hstride, hevT)) DoFilter2(p, hstride); else DoFilter4(p, hstride); } p += vstride; }
            }
            unsafe void FilterFrame()
            {
                fixed (byte* Y = yPlane) fixed (byte* U = uPlane) fixed (byte* V = vPlane)
                for (int mbY = 0; mbY < mbH; mbY++)
                    for (int mbX = 0; mbX < mbW; mbX++)
                    {
                        int k = mbY * mbW + mbX; int limit = mbFLimit[k]; if (limit == 0) continue;
                        int ilevel = mbFILevel[k], hevT = mbFHev[k]; bool inner = mbFInner[k] != 0;
                        int ys = yStride; byte* y = Y + mbY * 16 * ys + mbX * 16;
                        if (filterType == 1)
                        {
                            if (mbX > 0) SimpleHFilter16(y, ys, limit + 4);
                            if (inner) for (int q = 1; q <= 3; q++) SimpleHFilter16(y + 4 * q, ys, limit);
                            if (mbY > 0) SimpleVFilter16(y, ys, limit + 4);
                            if (inner) for (int q = 1; q <= 3; q++) SimpleVFilter16(y + 4 * q * ys, ys, limit);
                        }
                        else
                        {
                            int us = uvStride; byte* u = U + mbY * 8 * us + mbX * 8, v = V + mbY * 8 * us + mbX * 8;
                            if (mbX > 0)
                            {
                                FilterLoop26(y, 1, ys, 16, limit + 4, ilevel, hevT);
                                FilterLoop26(u, 1, us, 8, limit + 4, ilevel, hevT); FilterLoop26(v, 1, us, 8, limit + 4, ilevel, hevT);
                            }
                            if (inner)
                            {
                                for (int q = 1; q <= 3; q++) FilterLoop24(y + 4 * q, 1, ys, 16, limit, ilevel, hevT);
                                FilterLoop24(u + 4, 1, us, 8, limit, ilevel, hevT); FilterLoop24(v + 4, 1, us, 8, limit, ilevel, hevT);
                            }
                            if (mbY > 0)
                            {
                                FilterLoop26(y, ys, 1, 16, limit + 4, ilevel, hevT);
                                FilterLoop26(u, us, 1, 8, limit + 4, ilevel, hevT); FilterLoop26(v, us, 1, 8, limit + 4, ilevel, hevT);
                            }
                            if (inner)
                            {
                                for (int q = 1; q <= 3; q++) FilterLoop24(y + 4 * q * ys, ys, 1, 16, limit, ilevel, hevT);
                                FilterLoop24(u + 4 * us, us, 1, 8, limit, ilevel, hevT); FilterLoop24(v + 4 * us, us, 1, 8, limit, ilevel, hevT);
                            }
                        }
                    }
            }

            // ---- YUV 4:2:0 -> ARGB with libwebp's "fancy" (bilinear, 9-3-3-1) chroma upsampling
            [MethodImpl(MethodImplOptions.AggressiveInlining)] static int MultHi(int v, int coeff) => (v * coeff) >> 8;
            [MethodImpl(MethodImplOptions.AggressiveInlining)] static int Clip8Yuv(int v) => (v & ~((256 << 6) - 1)) == 0 ? v >> 6 : v < 0 ? 0 : 255;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static int YuvToArgb(int y, int u, int v)
            {
                int yy = MultHi(y, 19077);
                int r = Clip8Yuv(yy + MultHi(v, 26149) - 14234), g = Clip8Yuv(yy - MultHi(u, 6419) - MultHi(v, 13320) + 8708), b = Clip8Yuv(yy + MultHi(u, 33050) - 17685);
                return unchecked((int)0xFF000000) | r << 16 | g << 8 | b;
            }
            /// <summary>One line pair: top luma row + bottom luma row (or none) from the chroma rows above (top) and below (cur).</summary>
            void UpsampleLinePair(int topY, int botY, int topUV, int curUV, int[] dst, int topDst, int botDst, int len)
            {
                var y = yPlane; var u = uPlane; var v = vPlane;
                int lastPixelPair = (len - 1) >> 1;
                uint tlUV = (uint)(u[topUV] | v[topUV] << 16), lUV = (uint)(u[curUV] | v[curUV] << 16);
                {
                    uint uv0 = (3 * tlUV + lUV + 0x00020002u) >> 2;
                    dst[topDst] = YuvToArgb(y[topY], (int)(uv0 & 0xff), (int)(uv0 >> 16));
                    if (botY >= 0) { uint uv1 = (3 * lUV + tlUV + 0x00020002u) >> 2; dst[botDst] = YuvToArgb(y[botY], (int)(uv1 & 0xff), (int)(uv1 >> 16)); }
                }
                for (int x = 1; x <= lastPixelPair; x++)
                {
                    uint tUV = (uint)(u[topUV + x] | v[topUV + x] << 16), uv = (uint)(u[curUV + x] | v[curUV + x] << 16);
                    uint avg = tlUV + tUV + lUV + uv + 0x00080008u;
                    uint diag12 = (avg + 2 * (tUV + lUV)) >> 3, diag03 = (avg + 2 * (tlUV + uv)) >> 3;
                    {
                        uint uv0 = (diag12 + tlUV) >> 1, uv1 = (diag03 + tUV) >> 1;
                        dst[topDst + 2 * x - 1] = YuvToArgb(y[topY + 2 * x - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16));
                        dst[topDst + 2 * x] = YuvToArgb(y[topY + 2 * x], (int)(uv1 & 0xff), (int)(uv1 >> 16));
                    }
                    if (botY >= 0)
                    {
                        uint uv0 = (diag03 + lUV) >> 1, uv1 = (diag12 + uv) >> 1;
                        dst[botDst + 2 * x - 1] = YuvToArgb(y[botY + 2 * x - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16));
                        dst[botDst + 2 * x] = YuvToArgb(y[botY + 2 * x], (int)(uv1 & 0xff), (int)(uv1 >> 16));
                    }
                    tlUV = tUV; lUV = uv;
                }
                if ((len & 1) == 0)
                {
                    uint uv0 = (3 * tlUV + lUV + 0x00020002u) >> 2;
                    dst[topDst + len - 1] = YuvToArgb(y[topY + len - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16));
                    if (botY >= 0) { uint uv1 = (3 * lUV + tlUV + 0x00020002u) >> 2; dst[botDst + len - 1] = YuvToArgb(y[botY + len - 1], (int)(uv1 & 0xff), (int)(uv1 >> 16)); }
                }
            }
            int[] ToArgb()
            {
                int w = width, h = height; var dst = new int[w * h];
                // row 0 from chroma row 0 alone; then rows (2k-1, 2k) from chroma rows (k-1, k); an even height's last row from the last chroma row alone
                UpsampleLinePair(0, -1, 0, 0, dst, 0, 0, w);
                int y = 0, uvRow = 0;
                for (; y + 2 < h; y += 2)
                {
                    int topUV = uvRow * uvStride; uvRow++; int curUV = uvRow * uvStride;
                    UpsampleLinePair((y + 1) * yStride, (y + 2) * yStride, topUV, curUV, dst, (y + 1) * w, (y + 2) * w, w);
                }
                if ((h & 1) == 0 && h > 1) UpsampleLinePair((h - 1) * yStride, -1, uvRow * uvStride, uvRow * uvStride, dst, (h - 1) * w, 0, w);
                return dst;
            }
        }

        // ------------------------------------------------------------------------------------------- tables (libwebp)
        internal static readonly byte[] CoeffsProba0 = {
            128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
            128, 253, 136, 254, 255, 228, 219, 128, 128, 128, 128, 128, 189, 129, 242, 255, 227, 213, 255, 219, 128, 128, 128, 106, 126, 227, 252, 214, 209, 255, 255, 128,
            128, 128, 1, 98, 248, 255, 236, 226, 255, 255, 128, 128, 128, 181, 133, 238, 254, 221, 234, 255, 154, 128, 128, 128, 78, 134, 202, 247, 198, 180, 255, 219,
            128, 128, 128, 1, 185, 249, 255, 243, 255, 128, 128, 128, 128, 128, 184, 150, 247, 255, 236, 224, 128, 128, 128, 128, 128, 77, 110, 216, 255, 236, 230, 128,
            128, 128, 128, 128, 1, 101, 251, 255, 241, 255, 128, 128, 128, 128, 128, 170, 139, 241, 252, 236, 209, 255, 255, 128, 128, 128, 37, 116, 196, 243, 228, 255,
            255, 255, 128, 128, 128, 1, 204, 254, 255, 245, 255, 128, 128, 128, 128, 128, 207, 160, 250, 255, 238, 128, 128, 128, 128, 128, 128, 102, 103, 231, 255, 211,
            171, 128, 128, 128, 128, 128, 1, 152, 252, 255, 240, 255, 128, 128, 128, 128, 128, 177, 135, 243, 255, 234, 225, 128, 128, 128, 128, 128, 80, 129, 211, 255,
            194, 224, 128, 128, 128, 128, 128, 1, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128, 246, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128, 255, 128, 128,
            128, 128, 128, 128, 128, 128, 128, 128, 198, 35, 237, 223, 193, 187, 162, 160, 145, 155, 62, 131, 45, 198, 221, 172, 176, 220, 157, 252, 221, 1, 68, 47,
            146, 208, 149, 167, 221, 162, 255, 223, 128, 1, 149, 241, 255, 221, 224, 255, 255, 128, 128, 128, 184, 141, 234, 253, 222, 220, 255, 199, 128, 128, 128, 81,
            99, 181, 242, 176, 190, 249, 202, 255, 255, 128, 1, 129, 232, 253, 214, 197, 242, 196, 255, 255, 128, 99, 121, 210, 250, 201, 198, 255, 202, 128, 128, 128,
            23, 91, 163, 242, 170, 187, 247, 210, 255, 255, 128, 1, 200, 246, 255, 234, 255, 128, 128, 128, 128, 128, 109, 178, 241, 255, 231, 245, 255, 255, 128, 128,
            128, 44, 130, 201, 253, 205, 192, 255, 255, 128, 128, 128, 1, 132, 239, 251, 219, 209, 255, 165, 128, 128, 128, 94, 136, 225, 251, 218, 190, 255, 255, 128,
            128, 128, 22, 100, 174, 245, 186, 161, 255, 199, 128, 128, 128, 1, 182, 249, 255, 232, 235, 128, 128, 128, 128, 128, 124, 143, 241, 255, 227, 234, 128, 128,
            128, 128, 128, 35, 77, 181, 251, 193, 211, 255, 205, 128, 128, 128, 1, 157, 247, 255, 236, 231, 255, 255, 128, 128, 128, 121, 141, 235, 255, 225, 227, 255,
            255, 128, 128, 128, 45, 99, 188, 251, 195, 217, 255, 224, 128, 128, 128, 1, 1, 251, 255, 213, 255, 128, 128, 128, 128, 128, 203, 1, 248, 255, 255, 128,
            128, 128, 128, 128, 128, 137, 1, 177, 255, 224, 255, 128, 128, 128, 128, 128, 253, 9, 248, 251, 207, 208, 255, 192, 128, 128, 128, 175, 13, 224, 243, 193,
            185, 249, 198, 255, 255, 128, 73, 17, 171, 221, 161, 179, 236, 167, 255, 234, 128, 1, 95, 247, 253, 212, 183, 255, 255, 128, 128, 128, 239, 90, 244, 250,
            211, 209, 255, 255, 128, 128, 128, 155, 77, 195, 248, 188, 195, 255, 255, 128, 128, 128, 1, 24, 239, 251, 218, 219, 255, 205, 128, 128, 128, 201, 51, 219,
            255, 196, 186, 128, 128, 128, 128, 128, 69, 46, 190, 239, 201, 218, 255, 228, 128, 128, 128, 1, 191, 251, 255, 255, 128, 128, 128, 128, 128, 128, 223, 165,
            249, 255, 213, 255, 128, 128, 128, 128, 128, 141, 124, 248, 255, 255, 128, 128, 128, 128, 128, 128, 1, 16, 248, 255, 255, 128, 128, 128, 128, 128, 128, 190,
            36, 230, 255, 236, 255, 128, 128, 128, 128, 128, 149, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128, 1, 226, 255, 128, 128, 128, 128, 128, 128, 128, 128,
            247, 192, 255, 128, 128, 128, 128, 128, 128, 128, 128, 240, 128, 255, 128, 128, 128, 128, 128, 128, 128, 128, 1, 134, 252, 255, 255, 128, 128, 128, 128, 128,
            128, 213, 62, 250, 255, 255, 128, 128, 128, 128, 128, 128, 55, 93, 255, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
            128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 202, 24, 213, 235, 186, 191, 220, 160,
            240, 175, 255, 126, 38, 182, 232, 169, 184, 228, 174, 255, 187, 128, 61, 46, 138, 219, 151, 178, 240, 170, 255, 216, 128, 1, 112, 230, 250, 199, 191, 247,
            159, 255, 255, 128, 166, 109, 228, 252, 211, 215, 255, 174, 128, 128, 128, 39, 77, 162, 232, 172, 180, 245, 178, 255, 255, 128, 1, 52, 220, 246, 198, 199,
            249, 220, 255, 255, 128, 124, 74, 191, 243, 183, 193, 250, 221, 255, 255, 128, 24, 71, 130, 219, 154, 170, 243, 182, 255, 255, 128, 1, 182, 225, 249, 219,
            240, 255, 224, 128, 128, 128, 149, 150, 226, 252, 216, 205, 255, 171, 128, 128, 128, 28, 108, 170, 242, 183, 194, 254, 223, 255, 255, 128, 1, 81, 230, 252,
            204, 203, 255, 192, 128, 128, 128, 123, 102, 209, 247, 188, 196, 255, 233, 128, 128, 128, 20, 95, 153, 243, 164, 173, 255, 203, 128, 128, 128, 1, 222, 248,
            255, 216, 213, 128, 128, 128, 128, 128, 168, 175, 246, 252, 235, 205, 255, 255, 128, 128, 128, 47, 116, 215, 255, 211, 212, 255, 255, 128, 128, 128, 1, 121,
            236, 253, 212, 214, 255, 255, 128, 128, 128, 141, 84, 213, 252, 201, 202, 255, 219, 128, 128, 128, 42, 80, 160, 240, 162, 185, 255, 205, 128, 128, 128, 1,
            1, 255, 128, 128, 128, 128, 128, 128, 128, 128, 244, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128, 238, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128,
        };
        internal static readonly byte[] CoeffsUpdateProba = {
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 176, 246, 255, 255, 255, 255, 255, 255, 255, 255, 255, 223, 241, 252, 255, 255, 255, 255, 255, 255, 255, 255, 249, 253, 253, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 244, 252, 255, 255, 255, 255, 255, 255, 255, 255, 234, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 253, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 246, 254, 255, 255, 255, 255, 255, 255, 255, 255, 239, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 254, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 248, 254, 255, 255, 255, 255, 255, 255, 255, 255, 251, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 251, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 254, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 254, 253, 255, 254, 255, 255, 255, 255, 255, 255, 250, 255, 254, 255, 254, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 217, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 225, 252, 241, 253, 255, 255, 254, 255, 255, 255, 255, 234, 250,
            241, 250, 253, 255, 253, 254, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 223, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 238,
            253, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 248, 254, 255, 255, 255, 255, 255, 255, 255, 255, 249, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 253, 255, 255, 255, 255, 255, 255, 255, 255, 255, 247, 254, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 252, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 253, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 253, 255, 255, 255, 255, 255, 255, 255, 255, 250, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 186, 251, 250, 255, 255, 255, 255, 255, 255, 255, 255, 234, 251, 244, 254, 255,
            255, 255, 255, 255, 255, 255, 251, 251, 243, 253, 254, 255, 254, 255, 255, 255, 255, 255, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 236, 253, 254, 255,
            255, 255, 255, 255, 255, 255, 255, 251, 253, 253, 254, 254, 255, 255, 255, 255, 255, 255, 255, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 254, 254, 254,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 254,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 248, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 250, 254, 252, 254, 255, 255, 255, 255, 255, 255, 255, 248, 254, 249, 253, 255, 255, 255, 255, 255, 255, 255, 255, 253, 253, 255, 255, 255, 255,
            255, 255, 255, 255, 246, 253, 253, 255, 255, 255, 255, 255, 255, 255, 255, 252, 254, 251, 254, 254, 255, 255, 255, 255, 255, 255, 255, 254, 252, 255, 255, 255,
            255, 255, 255, 255, 255, 248, 254, 253, 255, 255, 255, 255, 255, 255, 255, 255, 253, 255, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 251, 254, 255, 255,
            255, 255, 255, 255, 255, 255, 245, 251, 254, 255, 255, 255, 255, 255, 255, 255, 255, 253, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 251, 253, 255,
            255, 255, 255, 255, 255, 255, 255, 252, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 252, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 249, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            253, 255, 255, 255, 255, 255, 255, 255, 255, 250, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        };
        internal static readonly byte[] BModesProba = {
            231, 120, 48, 89, 115, 113, 120, 152, 112, 152, 179, 64, 126, 170, 118, 46, 70, 95, 175, 69, 143, 80, 85, 82, 72, 155, 103, 56, 58, 10, 171, 218,
            189, 17, 13, 152, 114, 26, 17, 163, 44, 195, 21, 10, 173, 121, 24, 80, 195, 26, 62, 44, 64, 85, 144, 71, 10, 38, 171, 213, 144, 34, 26, 170,
            46, 55, 19, 136, 160, 33, 206, 71, 63, 20, 8, 114, 114, 208, 12, 9, 226, 81, 40, 11, 96, 182, 84, 29, 16, 36, 134, 183, 89, 137, 98, 101,
            106, 165, 148, 72, 187, 100, 130, 157, 111, 32, 75, 80, 66, 102, 167, 99, 74, 62, 40, 234, 128, 41, 53, 9, 178, 241, 141, 26, 8, 107, 74, 43,
            26, 146, 73, 166, 49, 23, 157, 65, 38, 105, 160, 51, 52, 31, 115, 128, 104, 79, 12, 27, 217, 255, 87, 17, 7, 87, 68, 71, 44, 114, 51, 15,
            186, 23, 47, 41, 14, 110, 182, 183, 21, 17, 194, 66, 45, 25, 102, 197, 189, 23, 18, 22, 88, 88, 147, 150, 42, 46, 45, 196, 205, 43, 97, 183,
            117, 85, 38, 35, 179, 61, 39, 53, 200, 87, 26, 21, 43, 232, 171, 56, 34, 51, 104, 114, 102, 29, 93, 77, 39, 28, 85, 171, 58, 165, 90, 98,
            64, 34, 22, 116, 206, 23, 34, 43, 166, 73, 107, 54, 32, 26, 51, 1, 81, 43, 31, 68, 25, 106, 22, 64, 171, 36, 225, 114, 34, 19, 21, 102,
            132, 188, 16, 76, 124, 62, 18, 78, 95, 85, 57, 50, 48, 51, 193, 101, 35, 159, 215, 111, 89, 46, 111, 60, 148, 31, 172, 219, 228, 21, 18, 111,
            112, 113, 77, 85, 179, 255, 38, 120, 114, 40, 42, 1, 196, 245, 209, 10, 25, 109, 88, 43, 29, 140, 166, 213, 37, 43, 154, 61, 63, 30, 155, 67,
            45, 68, 1, 209, 100, 80, 8, 43, 154, 1, 51, 26, 71, 142, 78, 78, 16, 255, 128, 34, 197, 171, 41, 40, 5, 102, 211, 183, 4, 1, 221, 51,
            50, 17, 168, 209, 192, 23, 25, 82, 138, 31, 36, 171, 27, 166, 38, 44, 229, 67, 87, 58, 169, 82, 115, 26, 59, 179, 63, 59, 90, 180, 59, 166,
            93, 73, 154, 40, 40, 21, 116, 143, 209, 34, 39, 175, 47, 15, 16, 183, 34, 223, 49, 45, 183, 46, 17, 33, 183, 6, 98, 15, 32, 183, 57, 46,
            22, 24, 128, 1, 54, 17, 37, 65, 32, 73, 115, 28, 128, 23, 128, 205, 40, 3, 9, 115, 51, 192, 18, 6, 223, 87, 37, 9, 115, 59, 77, 64,
            21, 47, 104, 55, 44, 218, 9, 54, 53, 130, 226, 64, 90, 70, 205, 40, 41, 23, 26, 57, 54, 57, 112, 184, 5, 41, 38, 166, 213, 30, 34, 26,
            133, 152, 116, 10, 32, 134, 39, 19, 53, 221, 26, 114, 32, 73, 255, 31, 9, 65, 234, 2, 15, 1, 118, 73, 75, 32, 12, 51, 192, 255, 160, 43,
            51, 88, 31, 35, 67, 102, 85, 55, 186, 85, 56, 21, 23, 111, 59, 205, 45, 37, 192, 55, 38, 70, 124, 73, 102, 1, 34, 98, 125, 98, 42, 88,
            104, 85, 117, 175, 82, 95, 84, 53, 89, 128, 100, 113, 101, 45, 75, 79, 123, 47, 51, 128, 81, 171, 1, 57, 17, 5, 71, 102, 57, 53, 41, 49,
            38, 33, 13, 121, 57, 73, 26, 1, 85, 41, 10, 67, 138, 77, 110, 90, 47, 114, 115, 21, 2, 10, 102, 255, 166, 23, 6, 101, 29, 16, 10, 85,
            128, 101, 196, 26, 57, 18, 10, 102, 102, 213, 34, 20, 43, 117, 20, 15, 36, 163, 128, 68, 1, 26, 102, 61, 71, 37, 34, 53, 31, 243, 192, 69,
            60, 71, 38, 73, 119, 28, 222, 37, 68, 45, 128, 34, 1, 47, 11, 245, 171, 62, 17, 19, 70, 146, 85, 55, 62, 70, 37, 43, 37, 154, 100, 163,
            85, 160, 1, 63, 9, 92, 136, 28, 64, 32, 201, 85, 75, 15, 9, 9, 64, 255, 184, 119, 16, 86, 6, 28, 5, 64, 255, 25, 248, 1, 56, 8,
            17, 132, 137, 255, 55, 116, 128, 58, 15, 20, 82, 135, 57, 26, 121, 40, 164, 50, 31, 137, 154, 133, 25, 35, 218, 51, 103, 44, 131, 131, 123, 31,
            6, 158, 86, 40, 64, 135, 148, 224, 45, 183, 128, 22, 26, 17, 131, 240, 154, 14, 1, 209, 45, 16, 21, 91, 64, 222, 7, 1, 197, 56, 21, 39,
            155, 60, 138, 23, 102, 213, 83, 12, 13, 54, 192, 255, 68, 47, 28, 85, 26, 85, 85, 128, 128, 32, 146, 171, 18, 11, 7, 63, 144, 171, 4, 4,
            246, 35, 27, 10, 146, 174, 171, 12, 26, 128, 190, 80, 35, 99, 180, 80, 126, 54, 45, 85, 126, 47, 87, 176, 51, 41, 20, 32, 101, 75, 128, 139,
            118, 146, 116, 128, 85, 56, 41, 15, 176, 236, 85, 37, 9, 62, 71, 30, 17, 119, 118, 255, 17, 18, 138, 101, 38, 60, 138, 55, 70, 43, 26, 142,
            146, 36, 19, 30, 171, 255, 97, 27, 20, 138, 45, 61, 62, 219, 1, 81, 188, 64, 32, 41, 20, 117, 151, 142, 20, 21, 163, 112, 19, 12, 61, 195,
            128, 48, 4, 24,
        };
        internal static readonly byte[] DcTable = {
            4, 5, 6, 7, 8, 9, 10, 10, 11, 12, 13, 14, 15, 16, 17, 17, 18, 19, 20, 20, 21, 21, 22, 22, 23, 23, 24, 25, 25, 26, 27, 28,
            29, 30, 31, 32, 33, 34, 35, 36, 37, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58,
            59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89,
            91, 93, 95, 96, 98, 100, 101, 102, 104, 106, 108, 110, 112, 114, 116, 118, 122, 124, 126, 128, 130, 132, 134, 136, 138, 140, 143, 145, 148, 151, 154, 157,
        };
        internal static readonly ushort[] AcTable = {
            4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35,
            36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 60, 62, 64, 66, 68, 70, 72, 74, 76,
            78, 80, 82, 84, 86, 88, 90, 92, 94, 96, 98, 100, 102, 104, 106, 108, 110, 112, 114, 116, 119, 122, 125, 128, 131, 134, 137, 140, 143, 146, 149, 152,
            155, 158, 161, 164, 167, 170, 173, 177, 181, 185, 189, 193, 197, 201, 205, 209, 213, 217, 221, 225, 229, 234, 239, 245, 249, 254, 259, 264, 269, 274, 279, 284,
        };
        internal static readonly byte[] CodeToPlane = {
            24, 7, 23, 25, 40, 6, 39, 41, 22, 26, 38, 42, 56, 5, 55, 57, 21, 27, 54, 58, 37, 43, 72, 4, 71, 73, 20, 28, 53, 59, 70, 74,
            36, 44, 88, 69, 75, 52, 60, 3, 87, 89, 19, 29, 86, 90, 35, 45, 68, 76, 85, 91, 51, 61, 104, 2, 103, 105, 18, 30, 102, 106, 34, 46,
            84, 92, 67, 77, 101, 107, 50, 62, 120, 1, 119, 121, 83, 93, 17, 31, 100, 108, 66, 78, 118, 122, 33, 47, 117, 123, 49, 63, 99, 109, 82, 94,
            0, 116, 124, 65, 79, 16, 32, 98, 110, 48, 115, 125, 81, 95, 64, 114, 126, 97, 111, 80, 113, 127, 96, 112,
        };
    }
}
