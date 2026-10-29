using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Sonix290
{
    /// <summary>A decoded frame: 8-bit planes, row-major, Planes = 1 (luminance) or 3 (R, G, B).</summary>
    public sealed class Picture
    {
        public int Width, Height, Planes;
        public byte[][] Plane;

        public double Mean(int plane = 0, int step = 4)
        {
            long sum = 0, n = 0;
            var p = Plane[plane];
            for (int i = 0; i < p.Length; i += step) { sum += p[i]; n++; }
            return n == 0 ? 0 : sum / (double)n;
        }

        /// <summary>Mean absolute difference to another picture (0 means a duplicate frame).</summary>
        public double MeanAbsDiff(Picture o, int step = 4)
        {
            if (o == null || o.Width != Width || o.Height != Height) return double.NaN;
            long sum = 0, n = 0;
            byte[] a = Plane[0], b = o.Plane[0];
            for (int i = 0; i < a.Length; i += step) { sum += Math.Abs(a[i] - b[i]); n++; }
            return sum / (double)n;
        }
    }

    public static class Imaging
    {
        // Huffman tables of ITU-T T.81 Annex K.3 (K.3.3.1 / K.3.3.2): code counts per length 1..16, then symbols.
        static readonly byte[] DcLumaCounts = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
        static readonly byte[] DcChromaCounts = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
        static readonly byte[] DcSymbols = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        static readonly byte[] AcLumaCounts = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
        static readonly byte[] AcLumaSymbols =
        {
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
            0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
            0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
            0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
            0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
            0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
            0xf9, 0xfa,
        };
        static readonly byte[] AcChromaCounts = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
        static readonly byte[] AcChromaSymbols =
        {
            0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
            0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
            0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
            0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
            0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
            0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
            0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
            0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
            0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
            0xf9, 0xfa,
        };

        static byte[] standardDht;

        /// <summary>One DHT segment (FFC4) holding the four Annex K tables: DC0, AC0, DC1, AC1.</summary>
        internal static byte[] StandardDht()
        {
            if (standardDht != null) return standardDht;
            var tables = new[]
            {
                (0x00, DcLumaCounts, DcSymbols), (0x10, AcLumaCounts, AcLumaSymbols),
                (0x01, DcChromaCounts, DcSymbols), (0x11, AcChromaCounts, AcChromaSymbols),
            };
            var seg = new MemoryStream();
            seg.WriteByte(0xFF); seg.WriteByte(0xC4);
            seg.WriteByte(0); seg.WriteByte(0);   // length, filled below
            foreach (var (id, counts, symbols) in tables)
            {
                seg.WriteByte((byte)id);
                seg.Write(counts, 0, 16);
                seg.Write(symbols, 0, symbols.Length);
            }
            var b = seg.ToArray();
            int len = b.Length - 2;
            b[2] = (byte)(len >> 8);
            b[3] = (byte)len;
            return standardDht = b;
        }

        /// <summary>
        /// UVC MJPEG frames often omit the Huffman tables and rely on the standard ones; still-image decoders
        /// may refuse such data. Inserts the standard tables before the scan when no DHT precedes it.
        /// </summary>
        public static byte[] WithHuffmanTables(byte[] j)
        {
            int i = 2;   // after SOI
            while (i + 4 <= j.Length && j[i] == 0xFF)
            {
                byte marker = j[i + 1];
                if (marker == 0xFF) { i++; continue; }   // fill byte
                if (marker == 0xC4) return j;
                if (marker == 0xDA)
                {
                    var dht = StandardDht();
                    var r = new byte[j.Length + dht.Length];
                    Buffer.BlockCopy(j, 0, r, 0, i);
                    Buffer.BlockCopy(dht, 0, r, i, dht.Length);
                    Buffer.BlockCopy(j, i, r, i + dht.Length, j.Length - i);
                    return r;
                }
                i += 2 + ((j[i + 2] << 8) | j[i + 3]);
            }
            return j;
        }

        /// <summary>Decodes a compressed (MJPEG) frame. Luminance uses Rec. 601 weights.</summary>
        public static Picture Decode(byte[] jpeg, bool color)
        {
            using (var ms = new MemoryStream(WithHuffmanTables(jpeg)))
            using (var bmp = new Bitmap(ms))
            {
                int w = bmp.Width, h = bmp.Height;
                var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var row = new byte[bits.Stride];
                    var pic = new Picture { Width = w, Height = h, Planes = color ? 3 : 1 };
                    pic.Plane = new byte[pic.Planes][];
                    for (int c = 0; c < pic.Planes; c++) pic.Plane[c] = new byte[w * h];
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, bits.Stride);
                        int o = y * w;
                        for (int x = 0, k = 0; x < w; x++, k += 3)
                        {
                            int bl = row[k], gr = row[k + 1], rd = row[k + 2];   // GDI+ stores BGR
                            if (color)
                            {
                                pic.Plane[0][o + x] = (byte)rd;
                                pic.Plane[1][o + x] = (byte)gr;
                                pic.Plane[2][o + x] = (byte)bl;
                            }
                            else
                                pic.Plane[0][o + x] = (byte)((rd * 299 + gr * 587 + bl * 114 + 500) / 1000);
                        }
                    }
                    return pic;
                }
                finally { bmp.UnlockBits(bits); }
            }
        }

        /// <summary>Decodes a YUY2 (Y0 U Y1 V) frame; colour by Rec. 601.</summary>
        public static Picture DecodeYuy2(byte[] d, int w, int h, bool color)
        {
            var pic = new Picture { Width = w, Height = h, Planes = color ? 3 : 1 };
            pic.Plane = new byte[pic.Planes][];
            for (int c = 0; c < pic.Planes; c++) pic.Plane[c] = new byte[w * h];
            for (int i = 0, px = 0; px + 1 < w * h && i + 3 < d.Length; i += 4, px += 2)
            {
                if (!color)
                {
                    pic.Plane[0][px] = d[i];
                    pic.Plane[0][px + 1] = d[i + 2];
                    continue;
                }
                int u = d[i + 1] - 128, v = d[i + 3] - 128;
                for (int k = 0; k < 2; k++)
                {
                    int y = d[i + 2 * k];
                    pic.Plane[0][px + k] = Clamp(y + 1.402 * v);
                    pic.Plane[1][px + k] = Clamp(y - 0.344 * u - 0.714 * v);
                    pic.Plane[2][px + k] = Clamp(y + 1.772 * u);
                }
            }
            return pic;
        }

        static byte Clamp(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v + 0.5);

        /// <summary>
        /// Cuts out a subframe in binned coordinates and bins by averaging. Result is ASCOM layout:
        /// [x, y] for one plane, [x, y, plane] for colour.
        /// </summary>
        public static Array ToAscom(Picture p, int startX, int startY, int numX, int numY, int bin)
        {
            int n = bin * bin;
            if (p.Planes == 1)
            {
                var a = new int[numX, numY];
                Fill(p.Plane[0], p.Width, startX, startY, numX, numY, bin, n, (x, y, v) => a[x, y] = v);
                return a;
            }
            var c = new int[numX, numY, p.Planes];
            for (int pl = 0; pl < p.Planes; pl++)
            {
                int plane = pl;
                Fill(p.Plane[pl], p.Width, startX, startY, numX, numY, bin, n, (x, y, v) => c[x, y, plane] = v);
            }
            return c;
        }

        static void Fill(byte[] src, int width, int startX, int startY, int numX, int numY, int bin, int n,
                         Action<int, int, int> put)
        {
            for (int y = 0; y < numY; y++)
            {
                int sy = (startY + y) * bin;
                for (int x = 0; x < numX; x++)
                {
                    int sx = (startX + x) * bin, sum = 0;
                    for (int dy = 0; dy < bin; dy++)
                    {
                        int o = (sy + dy) * width + sx;
                        for (int dx = 0; dx < bin; dx++) sum += src[o + dx];
                    }
                    put(x, y, (sum + n / 2) / n);
                }
            }
        }
    }
}
