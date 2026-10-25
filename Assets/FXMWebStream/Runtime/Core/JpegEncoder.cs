using System;

namespace FXM.WebStream
{
    /// <summary>
    /// Baseline JPEG encoder (sequential, 8-bit, 4:2:0) written entirely in C#.
    /// <para>
    /// Why not <c>ImageConversion.EncodeToJPG</c>? That Unity API must run on the main
    /// thread, so encoding would stall the game. This encoder does not touch any Unity API,
    /// so it is safe on a worker thread (see <see cref="AsyncJpegEncoder"/>), and the output
    /// still decodes with <c>ImageConversion.LoadImage</c> on the receiver.
    /// </para>
    /// <para>
    /// One instance is NOT thread-safe and keeps scratch buffers, so create one per worker
    /// thread. Output goes into a reusable buffer (<c>ref byte[] output</c>) to minimize
    /// GC allocations.
    /// </para>
    /// </summary>
    public sealed class JpegEncoder
    {
        public const int MinimumQuality = 1;
        public const int MaximumQuality = 100;

        // Zig-zag order: entry i is the natural (8x8 row-major) index of the i-th zig-zag coefficient.
        private static readonly int[] ZigZag =
        {
             0,  1,  8, 16,  9,  2,  3, 10,
            17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63
        };

        // Standard Annex K quantization tables (JPEG spec / libjpeg), natural order.
        private static readonly int[] LuminanceQuantBase =
        {
            16, 11, 10, 16, 24, 40, 51, 61,
            12, 12, 14, 19, 26, 58, 60, 55,
            14, 13, 16, 24, 40, 57, 69, 56,
            14, 17, 22, 29, 51, 87, 80, 62,
            18, 22, 37, 56, 68, 109, 103, 77,
            24, 35, 55, 64, 81, 104, 113, 92,
            49, 64, 78, 87, 103, 121, 120, 101,
            72, 92, 95, 98, 112, 100, 103, 99
        };

        private static readonly int[] ChrominanceQuantBase =
        {
            17, 18, 24, 47, 99, 99, 99, 99,
            18, 21, 26, 66, 99, 99, 99, 99,
            24, 26, 56, 99, 99, 99, 99, 99,
            47, 66, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99
        };

        // Standard Annex K Huffman tables. Bits = code count for each length 1..16.
        private static readonly byte[] LuminanceDcBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
        private static readonly byte[] LuminanceDcValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        private static readonly byte[] ChrominanceDcBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
        private static readonly byte[] ChrominanceDcValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

        private static readonly byte[] LuminanceAcBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
        private static readonly byte[] LuminanceAcValues =
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
            0xf9, 0xfa
        };

        private static readonly byte[] ChrominanceAcBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
        private static readonly byte[] ChrominanceAcValues =
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
            0xf9, 0xfa
        };

        private static readonly int[] LuminanceDcCodes = BuildHuffmanTable(LuminanceDcBits, LuminanceDcValues);
        private static readonly int[] LuminanceAcCodes = BuildHuffmanTable(LuminanceAcBits, LuminanceAcValues);
        private static readonly int[] ChrominanceDcCodes = BuildHuffmanTable(ChrominanceDcBits, ChrominanceDcValues);
        private static readonly int[] ChrominanceAcCodes = BuildHuffmanTable(ChrominanceAcBits, ChrominanceAcValues);

        // AAN scale factors (see jfdctflt.c). They correct the amplitude of the fast DCT.
        private static readonly float[] AanScale =
        {
            1.0f, 1.387039845f, 1.306562965f, 1.175875602f,
            1.0f, 0.785694958f, 0.541196100f, 0.275899379f
        };

        // Per-instance scratch buffers (reused every frame, never allocated per frame).
        private float[] _block = new float[64];
        private int[] _coefficients = new int[64];
        private int[] _quantLuminance = new int[64];
        private int[] _quantChrominance = new int[64];
        private float[] _factorLuminance = new float[64];
        private float[] _factorChrominance = new float[64];
        private byte[] _yPlane = Array.Empty<byte>();
        private byte[] _cbPlane = Array.Empty<byte>();
        private byte[] _crPlane = Array.Empty<byte>();

        private byte[] _output = Array.Empty<byte>();
        private int _outputPosition;
        private int _bitBuffer;
        private int _bitCount;

        /// <summary>This encoder supports only 4:2:0; exposed for diagnostics.</summary>
        public const int ChromaSubsampling = 2;

        /// <summary>
        /// Encode one frame as baseline JPEG.
        /// </summary>
        /// <param name="source">Source pixels, rows ordered bottom-to-top or top-to-bottom (JPEG follows the source row order).</param>
        /// <param name="width">Pixel width; at least 1.</param>
        /// <param name="height">Pixel height; at least 1.</param>
        /// <param name="sourceChannels">3 = RGB, 4 = RGBA (alpha is ignored).</param>
        /// <param name="quality">Quality 1..100.</param>
        /// <param name="output">Output buffer; replaced when too small. Return it to the pool after use.</param>
        /// <param name="flipVertically">
        /// True when the first row of <paramref name="source"/> is the BOTTOM row (the normal
        /// Unity texture / RenderTexture layout). JPEG stores the top row first, so this flag
        /// reverses the read order and keeps the image upright after ImageConversion.LoadImage.
        /// </param>
        /// <returns>Valid JPEG byte count inside <paramref name="output"/>.</returns>
        public int Encode(byte[] source, int width, int height, int sourceChannels, int quality, ref byte[] output, bool flipVertically = false)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException("width", "Frame size must be greater than 0.");
            }

            if (sourceChannels != 3 && sourceChannels != 4)
            {
                throw new ArgumentOutOfRangeException("sourceChannels", "Only RGB (3) or RGBA (4) is supported.");
            }

            if (source.Length < width * height * sourceChannels)
            {
                throw new ArgumentException("Source buffer is smaller than width * height * channels.", "source");
            }

            quality = Clamp(quality, MinimumQuality, MaximumQuality);

            // 4:2:0 needs multiples of 16; pad the remainder by replicating edge pixels.
            int paddedWidth = ((width + 15) / 16) * 16;
            int paddedHeight = ((height + 15) / 16) * 16;
            int chromaWidth = paddedWidth / 2;
            int chromaHeight = paddedHeight / 2;

            BuildQuantizationTables(quality);
            FillColorPlanes(source, width, height, sourceChannels, paddedWidth, paddedHeight, chromaWidth, chromaHeight, flipVertically);

            // Rough estimate so the buffer rarely grows mid-encode.
            // The output buffer belongs to the caller (usually pooled) and is reused.
            int estimatedSize = Math.Max(4096, width * height / 4);
            if (output == null || output.Length < estimatedSize)
            {
                output = new byte[estimatedSize];
            }

            _output = output;
            _outputPosition = 0;
            _bitBuffer = 0;
            _bitCount = 0;

            WriteHeaders(width, height);

            int luminanceDc = 0;
            int chrominanceCbDc = 0;
            int chrominanceCrDc = 0;
            int mcuWidth = paddedWidth / 16;
            int mcuHeight = paddedHeight / 16;

            for (int mcuY = 0; mcuY < mcuHeight; mcuY++)
            {
                for (int mcuX = 0; mcuX < mcuWidth; mcuX++)
                {
                    // Four luma blocks (2x2) per MCU.
                    for (int blockY = 0; blockY < 2; blockY++)
                    {
                        for (int blockX = 0; blockX < 2; blockX++)
                        {
                            LoadBlock(_yPlane, paddedWidth, mcuX * 16 + blockX * 8, mcuY * 16 + blockY * 8);
                            EncodeBlock(_factorLuminance, LuminanceDcCodes, LuminanceAcCodes, ref luminanceDc);
                        }
                    }

                    LoadBlock(_cbPlane, chromaWidth, mcuX * 8, mcuY * 8);
                    EncodeBlock(_factorChrominance, ChrominanceDcCodes, ChrominanceAcCodes, ref chrominanceCbDc);

                    LoadBlock(_crPlane, chromaWidth, mcuX * 8, mcuY * 8);
                    EncodeBlock(_factorChrominance, ChrominanceDcCodes, ChrominanceAcCodes, ref chrominanceCrDc);
                }
            }

            FlushBits();

            // EOI
            EnsureCapacity(2);
            _output[_outputPosition++] = 0xFF;
            _output[_outputPosition++] = 0xD9;

            output = _output;
            return _outputPosition;
        }

        private void FillColorPlanes(byte[] source, int width, int height, int channels,
            int paddedWidth, int paddedHeight, int chromaWidth, int chromaHeight, bool flipVertically)
        {
            int ySize = paddedWidth * paddedHeight;
            if (_yPlane.Length < ySize)
            {
                _yPlane = new byte[ySize];
            }

            int chromaSize = chromaWidth * chromaHeight;
            if (_cbPlane.Length < chromaSize)
            {
                _cbPlane = new byte[chromaSize];
                _crPlane = new byte[chromaSize];
            }

            // Luma: convert RGB to Y for every pixel (edges replicated / clamped).
            for (int y = 0; y < paddedHeight; y++)
            {
                int sourceY = y < height ? y : height - 1;
                if (flipVertically)
                {
                    sourceY = height - 1 - sourceY;
                }

                int destinationRow = y * paddedWidth;
                int sourceRow = sourceY * width;
                for (int x = 0; x < paddedWidth; x++)
                {
                    int sourceX = x < width ? x : width - 1;
                    int index = (sourceRow + sourceX) * channels;
                    int r = source[index];
                    int g = source[index + 1];
                    int b = source[index + 2];
                    _yPlane[destinationRow + x] = (byte)((77 * r + 150 * g + 29 * b) >> 8);
                }
            }

            // Chroma: average each 2x2 block, then convert (4:2:0 subsampling).
            for (int cy = 0; cy < chromaHeight; cy++)
            {
                int y0 = Math.Min(cy * 2, height - 1);
                int y1 = Math.Min(cy * 2 + 1, height - 1);
                if (flipVertically)
                {
                    y0 = height - 1 - y0;
                    y1 = height - 1 - y1;
                }

                int row0 = y0 * width;
                int row1 = y1 * width;
                int destinationRow = cy * chromaWidth;

                for (int cx = 0; cx < chromaWidth; cx++)
                {
                    int x0 = Math.Min(cx * 2, width - 1);
                    int x1 = Math.Min(cx * 2 + 1, width - 1);

                    int i0 = (row0 + x0) * channels;
                    int i1 = (row0 + x1) * channels;
                    int i2 = (row1 + x0) * channels;
                    int i3 = (row1 + x1) * channels;

                    int r = (source[i0] + source[i1] + source[i2] + source[i3]) >> 2;
                    int g = (source[i0 + 1] + source[i1 + 1] + source[i2 + 1] + source[i3 + 1]) >> 2;
                    int b = (source[i0 + 2] + source[i1 + 2] + source[i2 + 2] + source[i3 + 2]) >> 2;

                    int cb = 128 + ((-43 * r - 85 * g + 128 * b) >> 8);
                    int cr = 128 + ((128 * r - 107 * g - 21 * b) >> 8);

                    _cbPlane[destinationRow + cx] = (byte)Clamp(cb, 0, 255);
                    _crPlane[destinationRow + cx] = (byte)Clamp(cr, 0, 255);
                }
            }
        }

        private void BuildQuantizationTables(int quality)
        {
            // libjpeg quality scaling: quality below 50 enlarges the tables (higher compression).
            int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;

            for (int i = 0; i < 64; i++)
            {
                int luminance = (LuminanceQuantBase[i] * scale + 50) / 100;
                int chrominance = (ChrominanceQuantBase[i] * scale + 50) / 100;
                _quantLuminance[i] = Clamp(luminance, 1, 255);
                _quantChrominance[i] = Clamp(chrominance, 1, 255);
            }

            // Combine the quantization divisor with the AAN scale and the float DCT factor 8
            // (see jcdctmgr.c: divisor = quantval * aanscale[row] * aanscale[col] * 8).
            for (int row = 0; row < 8; row++)
            {
                for (int column = 0; column < 8; column++)
                {
                    int index = row * 8 + column;
                    _factorLuminance[index] = 1f / (_quantLuminance[index] * AanScale[row] * AanScale[column] * 8f);
                    _factorChrominance[index] = 1f / (_quantChrominance[index] * AanScale[row] * AanScale[column] * 8f);
                }
            }
        }

        private void WriteHeaders(int width, int height)
        {
            // SOI
            WriteMarker(0xD8);

            // APP0 JFIF 1.1 (tanpa thumbnail)
            WriteMarker(0xE0);
            WriteUInt16(16);
            WriteByte((byte)'J');
            WriteByte((byte)'F');
            WriteByte((byte)'I');
            WriteByte((byte)'F');
            WriteByte(0);
            WriteByte(1);
            WriteByte(1);
            WriteByte(0); // density unit = none
            WriteUInt16(1);
            WriteUInt16(1);
            WriteByte(0);
            WriteByte(0);

            // DQT luminance (id 0) and chrominance (id 1), values written in zig-zag order.
            WriteDqt(0, _quantLuminance);
            WriteDqt(1, _quantChrominance);

            // SOF0 baseline
            WriteMarker(0xC0);
            WriteUInt16(17);
            WriteByte(8);              // precision
            WriteUInt16(height);
            WriteUInt16(width);
            WriteByte(3);              // component count
            WriteByte(1);              // Y
            WriteByte(0x22);           // 2x2 sampling
            WriteByte(0);              // quant table 0
            WriteByte(2);              // Cb
            WriteByte(0x11);
            WriteByte(1);
            WriteByte(3);              // Cr
            WriteByte(0x11);
            WriteByte(1);

            // DHT: four standard tables
            WriteDht(0x00, LuminanceDcBits, LuminanceDcValues);
            WriteDht(0x10, LuminanceAcBits, LuminanceAcValues);
            WriteDht(0x01, ChrominanceDcBits, ChrominanceDcValues);
            WriteDht(0x11, ChrominanceAcBits, ChrominanceAcValues);

            // SOS
            WriteMarker(0xDA);
            WriteUInt16(12);
            WriteByte(3);
            WriteByte(1);
            WriteByte(0x00); // DC 0 / AC 0
            WriteByte(2);
            WriteByte(0x11); // DC 1 / AC 1
            WriteByte(3);
            WriteByte(0x11);
            WriteByte(0);  // Ss
            WriteByte(63); // Se
            WriteByte(0);  // Ah / Al
        }

        private void WriteDqt(int tableId, int[] values)
        {
            WriteMarker(0xDB);
            WriteUInt16(2 + 1 + 64);
            WriteByte((byte)tableId); // 8-bit precision, table id
            for (int i = 0; i < 64; i++)
            {
                WriteByte((byte)values[ZigZag[i]]);
            }
        }

        private void WriteDht(byte tableClassAndId, byte[] bits, byte[] values)
        {
            WriteMarker(0xC4);
            WriteUInt16(2 + 1 + 16 + values.Length);
            WriteByte(tableClassAndId);
            for (int i = 0; i < 16; i++)
            {
                WriteByte(bits[i]);
            }

            for (int i = 0; i < values.Length; i++)
            {
                WriteByte(values[i]);
            }
        }

        private void LoadBlock(byte[] plane, int planeWidth, int x0, int y0)
        {
            for (int y = 0; y < 8; y++)
            {
                int row = (y0 + y) * planeWidth + x0;
                int blockRow = y * 8;
                for (int x = 0; x < 8; x++)
                {
                    _block[blockRow + x] = plane[row + x] - 128;
                }
            }
        }

        private void EncodeBlock(float[] quantFactor, int[] dcCodes, int[] acCodes, ref int previousDc)
        {
            ForwardDct(_block);

            // Quantize and reorder to zig-zag in one pass.
            for (int i = 0; i < 64; i++)
            {
                int natural = ZigZag[i];
                float value = _block[natural] * quantFactor[natural];
                _coefficients[i] = (int)(value + (value >= 0f ? 0.5f : -0.5f));
            }

            int diff = _coefficients[0] - previousDc;
            previousDc = _coefficients[0];

            int dcBits = BitLength(diff);
            WriteHuffman(dcCodes, dcBits);
            if (dcBits > 0)
            {
                WriteBits(diff < 0 ? diff + (1 << dcBits) - 1 : diff, dcBits);
            }

            int runLength = 0;
            for (int i = 1; i < 64; i++)
            {
                int coefficient = _coefficients[i];
                if (coefficient == 0)
                {
                    runLength++;
                    continue;
                }

                while (runLength > 15)
                {
                    WriteHuffman(acCodes, 0xF0); // ZRL: 16 zero coefficients
                    runLength -= 16;
                }

                int bits = BitLength(coefficient);
                WriteHuffman(acCodes, (runLength << 4) | bits);
                WriteBits(coefficient < 0 ? coefficient + (1 << bits) - 1 : coefficient, bits);
                runLength = 0;
            }

            if (runLength > 0)
            {
                WriteHuffman(acCodes, 0x00); // EOB
            }
        }

        /// <summary>
        /// Fast two-pass 8x8 DCT (AAN, floating point). Not scaled; the scale is compensated
        /// in the quantization tables built by <see cref="BuildQuantizationTables"/>.
        /// </summary>
        private void ForwardDct(float[] data)
        {
            // Pass 1: rows.
            for (int row = 0; row < 8; row++)
            {
                int offset = row * 8;

                float tmp0 = data[offset + 0] + data[offset + 7];
                float tmp7 = data[offset + 0] - data[offset + 7];
                float tmp1 = data[offset + 1] + data[offset + 6];
                float tmp6 = data[offset + 1] - data[offset + 6];
                float tmp2 = data[offset + 2] + data[offset + 5];
                float tmp5 = data[offset + 2] - data[offset + 5];
                float tmp3 = data[offset + 3] + data[offset + 4];
                float tmp4 = data[offset + 3] - data[offset + 4];

                float tmp10 = tmp0 + tmp3;
                float tmp13 = tmp0 - tmp3;
                float tmp11 = tmp1 + tmp2;
                float tmp12 = tmp1 - tmp2;

                data[offset + 0] = tmp10 + tmp11;
                data[offset + 4] = tmp10 - tmp11;

                float z1 = (tmp12 + tmp13) * 0.707106781f;
                data[offset + 2] = tmp13 + z1;
                data[offset + 6] = tmp13 - z1;

                tmp10 = tmp4 + tmp5;
                tmp11 = tmp5 + tmp6;
                tmp12 = tmp6 + tmp7;

                float z5 = (tmp10 - tmp12) * 0.382683433f;
                float z2 = 0.541196100f * tmp10 + z5;
                float z4 = 1.306562965f * tmp12 + z5;
                float z3 = tmp11 * 0.707106781f;

                float z11 = tmp7 + z3;
                float z13 = tmp7 - z3;

                data[offset + 5] = z13 + z2;
                data[offset + 3] = z13 - z2;
                data[offset + 1] = z11 + z4;
                data[offset + 7] = z11 - z4;
            }

            // Pass 2: columns.
            for (int column = 0; column < 8; column++)
            {
                float tmp0 = data[column + 0] + data[column + 56];
                float tmp7 = data[column + 0] - data[column + 56];
                float tmp1 = data[column + 8] + data[column + 48];
                float tmp6 = data[column + 8] - data[column + 48];
                float tmp2 = data[column + 16] + data[column + 40];
                float tmp5 = data[column + 16] - data[column + 40];
                float tmp3 = data[column + 24] + data[column + 32];
                float tmp4 = data[column + 24] - data[column + 32];

                float tmp10 = tmp0 + tmp3;
                float tmp13 = tmp0 - tmp3;
                float tmp11 = tmp1 + tmp2;
                float tmp12 = tmp1 - tmp2;

                data[column + 0] = tmp10 + tmp11;
                data[column + 32] = tmp10 - tmp11;

                float z1 = (tmp12 + tmp13) * 0.707106781f;
                data[column + 16] = tmp13 + z1;
                data[column + 48] = tmp13 - z1;

                tmp10 = tmp4 + tmp5;
                tmp11 = tmp5 + tmp6;
                tmp12 = tmp6 + tmp7;

                float z5 = (tmp10 - tmp12) * 0.382683433f;
                float z2 = 0.541196100f * tmp10 + z5;
                float z4 = 1.306562965f * tmp12 + z5;
                float z3 = tmp11 * 0.707106781f;

                float z11 = tmp7 + z3;
                float z13 = tmp7 - z3;

                data[column + 40] = z13 + z2;
                data[column + 24] = z13 - z2;
                data[column + 8] = z11 + z4;
                data[column + 56] = z11 - z4;
            }
        }

        private void WriteHuffman(int[] codes, int symbol)
        {
            int packed = codes[symbol];
            WriteBits(packed >> 8, packed & 0xFF);
        }

        private void WriteBits(int value, int bitCount)
        {
            if (bitCount <= 0)
            {
                return;
            }

            _bitBuffer = (_bitBuffer << bitCount) | (value & ((1 << bitCount) - 1));
            _bitCount += bitCount;

            while (_bitCount >= 8)
            {
                _bitCount -= 8;
                EmitByte((byte)((_bitBuffer >> _bitCount) & 0xFF));
            }
        }

        private void FlushBits()
        {
            if (_bitCount > 0)
            {
                // Pad remaining bits with zeros (standard JPEG padding).
                EmitByte((byte)((_bitBuffer << (8 - _bitCount)) & 0xFF));
                _bitBuffer = 0;
                _bitCount = 0;
            }
        }

        private void EmitByte(byte value)
        {
            EnsureCapacity(1);
            _output[_outputPosition++] = value;

            // Byte stuffing: 0xFF inside an entropy-coded segment must be followed by 0x00.
            if (value == 0xFF)
            {
                EnsureCapacity(1);
                _output[_outputPosition++] = 0x00;
            }
        }

        private void WriteMarker(byte marker)
        {
            EnsureCapacity(2);
            _output[_outputPosition++] = 0xFF;
            _output[_outputPosition++] = marker;
        }

        private void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _output[_outputPosition++] = value;
        }

        private void WriteUInt16(int value)
        {
            EnsureCapacity(2);
            _output[_outputPosition++] = (byte)(value >> 8);
            _output[_outputPosition++] = (byte)value;
        }

        private void EnsureCapacity(int extraBytes)
        {
            int required = _outputPosition + extraBytes;
            if (_output.Length >= required)
            {
                return;
            }

            int size = Math.Max(_output.Length * 2, 4096);
            while (size < required)
            {
                size *= 2;
            }

            var grown = new byte[size];
            Buffer.BlockCopy(_output, 0, grown, 0, _outputPosition);
            _output = grown;
        }

        private static int[] BuildHuffmanTable(byte[] bits, byte[] values)
        {
            var codes = new int[256];
            int code = 0;
            int index = 0;

            for (int length = 1; length <= 16; length++)
            {
                for (int i = 0; i < bits[length - 1]; i++)
                {
                    int symbol = values[index++];
                    codes[symbol] = (code << 8) | length;
                    code++;
                }

                code <<= 1;
            }

            return codes;
        }

        private static int BitLength(int value)
        {
            if (value < 0)
            {
                value = -value;
            }

            int bits = 0;
            while (value != 0)
            {
                bits++;
                value >>= 1;
            }

            return bits;
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
