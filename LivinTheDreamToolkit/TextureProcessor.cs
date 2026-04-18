using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ZstdSharp;

namespace TomodachiCanvasExport;

public static class TextureProcessor
{
    public const int DefaultBlockHeight = 16;
    public const int ThumbBlockHeight = 8;

    public const int ZstdLevel = 3;

    public enum TextureKind
    {
        Canvas,   // .canvas.zs          256x256 RGBA8
        Ugctex,   // .ugctex.zs          512x512 BC1 (most thing) or 384x384 BC1 (food, hopefully nothing else...)
        Thumb,    // _Thumb_ugctex.zs    256x256 BC3
    }

    public static TextureKind DetectKind(string fileName)
    {
        string lower = Path.GetFileName(fileName).ToLowerInvariant();
        if (lower.Contains("thumb")) return TextureKind.Thumb;
        if (lower.Contains("ugctex")) return TextureKind.Ugctex;
        return TextureKind.Canvas;
    }

    public enum TextureFormat { Bc1, Bc3 }

    public record UgctexLayout(
        int Width,
        int Height,
        int SwizzleBlocksWide,
        int SwizzleBlocksTall,
        int BlockHeight,
        TextureFormat Format)
    {
        public int BytesPerBlock => Format == TextureFormat.Bc3 ? 16 : 8;
    }

    public static UgctexLayout DetectUgctexLayout(int decompressedBytes)
    {
        return decompressedBytes switch
        {
            131072 => new UgctexLayout(512, 512, 128, 128, 16, TextureFormat.Bc1),
            98304 => new UgctexLayout(384, 384, 96, 128, 16, TextureFormat.Bc1),
            65536 => new UgctexLayout(256, 256, 64, 64, 8, TextureFormat.Bc3),
            _ => throw new InvalidDataException(
                $"Unknown ugctex format: {decompressedBytes} bytes decompressed. " +
                "Known sizes: 131072 (512x512 BC1 face paint), " +
                "98304 (384x384 BC1 food/goods), 65536 (256x256 BC3)."),
        };
    }

    public static Image<Rgba32> DecodeFile(string path, bool noSrgb = false)
    {
        TextureKind kind = DetectKind(path);
        byte[] rawData = ZstdDecompress(path);

        return kind switch
        {
            TextureKind.Thumb => DecodeThumb(rawData, noSrgb),
            TextureKind.Ugctex => DecodeUgctex(rawData, noSrgb),
            _ => DecodeCanvas(rawData, noSrgb),
        };
    }

    public static void ExportFileToPngs(string inputPath, bool noSrgb, Action<string>? log = null)
    {
        log ??= _ => { };

        string basePath = Path.Combine(
            Path.GetDirectoryName(inputPath) ?? ".",
            GetBaseName(Path.GetFileName(inputPath)));

        using var img = DecodeFile(inputPath, noSrgb);
        WriteExportPngs(img, basePath, log);
    }

    private static Image<Rgba32> DecodeCanvas(byte[] rawData, bool noSrgb)
    {
        int totalPixels = rawData.Length / 4;
        int width, height;

        int side = (int)Math.Sqrt(totalPixels);
        if (side * side == totalPixels)
        {
            width = height = side;
        }
        else
        {
            width = 256;
            height = totalPixels / width;
        }

        int expected = width * height * 4;
        if (rawData.Length != expected)
            throw new InvalidDataException(
                $"Unexpected decompressed size: got {rawData.Length} bytes, " +
                "cannot determine RGBA8 dimensions");

        byte[] rgba = DeswizzleBlockLinear(rawData, width, height, 4, DefaultBlockHeight);
        if (!noSrgb) ConvertLinearToSrgb(rgba);

        return Image.LoadPixelData<Rgba32>(rgba, width, height);
    }

    private static Image<Rgba32> DecodeUgctex(byte[] rawData, bool noSrgb)
    {
        UgctexLayout layout = DetectUgctexLayout(rawData.Length);

        int visibleBlocksWide = layout.Width / 4;
        int visibleBlocksTall = layout.Height / 4;
        byte[] blocks = DeswizzleBlockLinear(
            rawData,
            visibleBlocksWide,
            visibleBlocksTall,
            layout.BytesPerBlock,
            layout.BlockHeight);

        byte[] rgba = layout.Format == TextureFormat.Bc3
            ? Bc3Decode(blocks, layout.Width, layout.Height)
            : Bc1Decode(blocks, layout.Width, layout.Height);
        if (!noSrgb) ConvertLinearToSrgb(rgba);

        return Image.LoadPixelData<Rgba32>(rgba, layout.Width, layout.Height);
    }

    private static Image<Rgba32> DecodeThumb(byte[] rawData, bool noSrgb)
    {
        int totalBlocks = rawData.Length / 16;
        int gridSide = (int)Math.Sqrt(totalBlocks);

        if (gridSide * gridSide != totalBlocks)
            throw new InvalidDataException(
                $"Unexpected decompressed size: got {rawData.Length} bytes, " +
                $"not a square BC3 texture ({totalBlocks} blocks)");

        int texWidth = gridSide * 4;
        int texHeight = gridSide * 4;

        byte[] blocks = DeswizzleBlockLinear(rawData, gridSide, gridSide, 16, ThumbBlockHeight);
        byte[] rgba = Bc3Decode(blocks, texWidth, texHeight);
        if (!noSrgb) ConvertLinearToSrgb(rgba);

        return Image.LoadPixelData<Rgba32>(rgba, texWidth, texHeight);
    }
    public static void ImportPng(
        string pngPath,
        string destStem,
        bool writeThumb,
        bool noSrgb,
        Action<string>? log = null)
    {
        ImportPng(
            pngPath: pngPath,
            destStem: destStem,
            writeCanvas: true,
            writeThumb: writeThumb,
            noSrgb: noSrgb,
            originalUgctexPath: null,
            log: log);
    }

    public static void ImportPng(
        string pngPath,
        string destStem,
        bool writeCanvas,
        bool writeThumb,
        bool noSrgb,
        string? originalUgctexPath,
        Action<string>? log = null)
    {
        log ??= _ => { };

        using var srcImage = Image.Load<Rgba32>(pngPath);
        int srcWidth = srcImage.Width;
        int srcHeight = srcImage.Height;
        log($"Source image: {srcWidth}x{srcHeight}");

        UgctexLayout layout;
        byte[]? originalSwizzled = null;
        if (originalUgctexPath is not null && File.Exists(originalUgctexPath))
        {
            originalSwizzled = ZstdDecompress(originalUgctexPath);
            layout = DetectUgctexLayout(originalSwizzled.Length);
            log($"Detected target layout from {Path.GetFileName(originalUgctexPath)}: " +
                $"{layout.Width}x{layout.Height} BC1 (raw {layout} bytes)");
        }
        else
        {
            layout = new UgctexLayout(512, 512, 128, 128, 16, TextureFormat.Bc1);
        }

        if (writeCanvas)
        {
            const int canvasW = 256, canvasH = 256;

            using var canvasImage = srcImage.Clone(ctx =>
            {
                if (srcWidth != canvasW || srcHeight != canvasH)
                    ctx.Resize(canvasW, canvasH);
            });

            byte[] canvasRgba = new byte[canvasW * canvasH * 4];
            canvasImage.CopyPixelDataTo(canvasRgba);

            if (!noSrgb) ConvertSrgbToLinear(canvasRgba);

            byte[] canvasSwizzled = SwizzleBlockLinear(canvasRgba, canvasW, canvasH, 4, DefaultBlockHeight);

            string canvasPath = destStem + ".canvas.zs";
            File.WriteAllBytes(canvasPath, ZstdCompress(canvasSwizzled, ZstdLevel));
            log($"Wrote {canvasPath}");
        }

        {
            int ugcW = layout.Width;
            int ugcH = layout.Height;

            using var ugcImage = srcImage.Clone(ctx =>
            {
                if (srcWidth != ugcW || srcHeight != ugcH)
                    ctx.Resize(ugcW, ugcH);
            });

            byte[] ugcRgba = new byte[ugcW * ugcH * 4];
            ugcImage.CopyPixelDataTo(ugcRgba);

            if (!noSrgb) ConvertSrgbToLinear(ugcRgba);

            byte[] encodedBlocks = layout.Format == TextureFormat.Bc3
                ? Bc3Encode(ugcRgba, ugcW, ugcH)
                : Bc1Encode(ugcRgba, ugcW, ugcH);


            int visibleBlocksWide = layout.Width / 4;
            int visibleBlocksTall = layout.Height / 4;
            byte[] ugcSwizzled = SwizzleBlockLinear(
                encodedBlocks,
                visibleBlocksWide,
                visibleBlocksTall,
                layout.BytesPerBlock,
                layout.BlockHeight,
                baseBuffer: originalSwizzled);

            string ugcPath = destStem + ".ugctex.zs";
            File.WriteAllBytes(ugcPath, ZstdCompress(ugcSwizzled, ZstdLevel));
            log($"Wrote {ugcPath} ({layout.Width}x{layout.Height} {layout.Format})");
        }

        if (writeThumb)
        {
            const int thumbW = 256, thumbH = 256;

            using var thumbImage = srcImage.Clone(ctx =>
            {
                if (srcWidth != thumbW || srcHeight != thumbH)
                    ctx.Resize(thumbW, thumbH);
            });

            byte[] thumbRgba = new byte[thumbW * thumbH * 4];
            thumbImage.CopyPixelDataTo(thumbRgba);

            if (!noSrgb) ConvertSrgbToLinear(thumbRgba);

            byte[] bc3Blocks = Bc3Encode(thumbRgba, thumbW, thumbH);

            int gridW = thumbW / 4;
            int gridH = thumbH / 4;
            byte[] thumbSwizzled = SwizzleBlockLinear(bc3Blocks, gridW, gridH, 16, ThumbBlockHeight);

            string thumbPath = destStem + "_Thumb_ugctex.zs";
            File.WriteAllBytes(thumbPath, ZstdCompress(thumbSwizzled, ZstdLevel));
            log($"Wrote {thumbPath}");
        }
    }

    public static void WriteExportPngs(Image<Rgba32> img, string basePath, Action<string>? log = null)
    {
        log ??= _ => { };
        int width = img.Width;
        int height = img.Height;

        string rgbaPath = basePath + ".rgba.png";
        img.Save(rgbaPath);
        log($"Wrote {rgbaPath}");

        using var white = new Image<Rgba32>(width, height, new Rgba32(255, 255, 255, 255));
        white.Mutate(ctx => ctx.DrawImage(img, 1f));
        string whitePath = basePath + ".white.png";
        white.Save(whitePath);
        log($"Wrote {whitePath}");

        using var black = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 255));
        black.Mutate(ctx => ctx.DrawImage(img, 1f));
        string blackPath = basePath + ".black.png";
        black.Save(blackPath);
        log($"Wrote {blackPath}");

        using var alpha = new Image<L8>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                alpha[x, y] = new L8(img[x, y].A);
            }
        }
        string alphaPath = basePath + ".alpha.png";
        alpha.Save(alphaPath);
        log($"Wrote {alphaPath}");
    }
    public static string GetBaseName(string fileName)
    {
        string name = fileName;
        if (name.EndsWith(".zs", StringComparison.OrdinalIgnoreCase))
            name = name[..^3];
        foreach (string suffix in new[] { ".canvas", ".ugctex", "_canvas", "_ugctex" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }
        return name;
    }

    private static readonly byte[] SrgbToLinearLut = BuildSrgbToLinearLut();
    private static readonly byte[] LinearToSrgbLut = BuildLinearToSrgbLut();

    private static byte[] BuildSrgbToLinearLut()
    {
        byte[] lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double s = i / 255.0;
            double lin = s <= 0.04045
                ? s / 12.92
                : Math.Pow((s + 0.055) / 1.055, 2.4);
            lut[i] = (byte)Math.Clamp((int)Math.Round(lin * 255.0), 0, 255);
        }
        return lut;
    }

    private static byte[] BuildLinearToSrgbLut()
    {
        byte[] lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double lin = i / 255.0;
            double s = lin <= 0.0031308
                ? lin * 12.92
                : 1.055 * Math.Pow(lin, 1.0 / 2.4) - 0.055;
            lut[i] = (byte)Math.Clamp((int)Math.Round(s * 255.0), 0, 255);
        }
        return lut;
    }

    private static void ConvertSrgbToLinear(byte[] rgba)
    {
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = SrgbToLinearLut[rgba[i]];
            rgba[i + 1] = SrgbToLinearLut[rgba[i + 1]];
            rgba[i + 2] = SrgbToLinearLut[rgba[i + 2]];
        }
    }

    private static void ConvertLinearToSrgb(byte[] rgba)
    {
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = LinearToSrgbLut[rgba[i]];
            rgba[i + 1] = LinearToSrgbLut[rgba[i + 1]];
            rgba[i + 2] = LinearToSrgbLut[rgba[i + 2]];
        }
    }

    public static byte[] ZstdDecompress(string path)
    {
        byte[] compressed = File.ReadAllBytes(path);
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(compressed).ToArray();
    }

    public static byte[] ZstdCompress(byte[] data, int level)
    {
        using var compressor = new Compressor(level);
        return compressor.Wrap(data).ToArray();
    }

    private static int DivRoundUp(int n, int d) => (n + d - 1) / d;

    private static int GobAddress(int x, int y, int widthInGobs, int bytesPerElement, int blockHeight)
    {
        int xBytes = x * bytesPerElement;

        int gobAddress =
              (y / (8 * blockHeight)) * 512 * blockHeight * widthInGobs
            + (xBytes / 64) * 512 * blockHeight
            + ((y % (8 * blockHeight)) / 8) * 512;

        int xInGob = xBytes % 64;
        int yInGob = y % 8;

        return gobAddress
            + ((xInGob % 64) / 32) * 256
            + ((yInGob % 8) / 2) * 64
            + ((xInGob % 32) / 16) * 32
            + (yInGob % 2) * 16
            + (xInGob % 16);
    }

    private static byte[] DeswizzleBlockLinear(byte[] data, int width, int height, int bpe, int blockHeight)
    {
        int widthInGobs = DivRoundUp(width * bpe, 64);
        int paddedHeight = DivRoundUp(height, 8 * blockHeight) * (8 * blockHeight);
        int paddedSize = widthInGobs * paddedHeight * 64;
        byte[] source;
        if (data.Length >= paddedSize)
        {
            source = data;
        }
        else
        {
            source = new byte[paddedSize];
            Buffer.BlockCopy(data, 0, source, 0, data.Length);
        }

        byte[] output = new byte[width * height * bpe];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int swizzled = GobAddress(x, y, widthInGobs, bpe, blockHeight);
                int linear = (y * width + x) * bpe;
                Buffer.BlockCopy(source, swizzled, output, linear, bpe);
            }
        }

        return output;
    }

    private static byte[] SwizzleBlockLinear(byte[] data, int width, int height, int bpe, int blockHeight, byte[]? baseBuffer = null)
    {
        int widthInGobs = DivRoundUp(width * bpe, 64);
        int paddedHeight = DivRoundUp(height, 8 * blockHeight) * (8 * blockHeight);
        int paddedSize = widthInGobs * paddedHeight * 64;

        byte[] output;
        if (baseBuffer != null && baseBuffer.Length == paddedSize)
        {
            output = (byte[])baseBuffer.Clone();
        }
        else
        {
            output = new byte[paddedSize];
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int linear = (y * width + x) * bpe;
                int swizzled = GobAddress(x, y, widthInGobs, bpe, blockHeight);
                Buffer.BlockCopy(data, linear, output, swizzled, bpe);
            }
        }

        return output;
    }

    private static (byte R, byte G, byte B) Rgb565Decode(ushort c)
    {
        byte r = (byte)(((c >> 11) & 0x1F) * 255 / 31);
        byte g = (byte)(((c >> 5) & 0x3F) * 255 / 63);
        byte b = (byte)((c & 0x1F) * 255 / 31);
        return (r, g, b);
    }

    private static ushort Rgb565Encode(byte r, byte g, byte b)
    {
        int r5 = (r * 31 + 127) / 255;
        int g6 = (g * 63 + 127) / 255;
        int b5 = (b * 31 + 127) / 255;
        return (ushort)((r5 << 11) | (g6 << 5) | b5);
    }

    private static byte[] Bc1Decode(byte[] blockData, int texWidth, int texHeight)
    {
        int blocksX = texWidth / 4;
        int blocksY = texHeight / 4;
        byte[] output = new byte[texWidth * texHeight * 4];
        byte[] palette = new byte[16];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int blockOffset = (by * blocksX + bx) * 8;

                ushort c0Raw = BitConverter.ToUInt16(blockData, blockOffset);
                ushort c1Raw = BitConverter.ToUInt16(blockData, blockOffset + 2);
                uint indices = BitConverter.ToUInt32(blockData, blockOffset + 4);

                var (r0, g0, b0) = Rgb565Decode(c0Raw);
                var (r1, g1, b1) = Rgb565Decode(c1Raw);

                palette[0] = r0; palette[1] = g0; palette[2] = b0; palette[3] = 255;
                palette[4] = r1; palette[5] = g1; palette[6] = b1; palette[7] = 255;

                if (c0Raw > c1Raw)
                {
                    palette[8] = (byte)((2 * r0 + r1) / 3);
                    palette[9] = (byte)((2 * g0 + g1) / 3);
                    palette[10] = (byte)((2 * b0 + b1) / 3);
                    palette[11] = 255;
                    palette[12] = (byte)((r0 + 2 * r1) / 3);
                    palette[13] = (byte)((g0 + 2 * g1) / 3);
                    palette[14] = (byte)((b0 + 2 * b1) / 3);
                    palette[15] = 255;
                }
                else
                {
                    palette[8] = (byte)((r0 + r1) / 2);
                    palette[9] = (byte)((g0 + g1) / 2);
                    palette[10] = (byte)((b0 + b1) / 2);
                    palette[11] = 255;
                    palette[12] = 0;
                    palette[13] = 0;
                    palette[14] = 0;
                    palette[15] = 0;
                }

                for (int row = 0; row < 4; row++)
                {
                    for (int col = 0; col < 4; col++)
                    {
                        int idx = (int)((indices >> (2 * (row * 4 + col))) & 0x3);
                        int px = bx * 4 + col;
                        int py = by * 4 + row;
                        int dst = (py * texWidth + px) * 4;
                        int palOff = idx * 4;
                        output[dst] = palette[palOff];
                        output[dst + 1] = palette[palOff + 1];
                        output[dst + 2] = palette[palOff + 2];
                        output[dst + 3] = palette[palOff + 3];
                    }
                }
            }
        }

        return output;
    }

    private static byte[] Bc1Encode(byte[] rgba, int texWidth, int texHeight)
    {
        int blocksX = texWidth / 4;
        int blocksY = texHeight / 4;
        byte[] output = new byte[blocksX * blocksY * 8];
        byte[] block = new byte[64];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                bool hasAlpha = false;

                for (int row = 0; row < 4; row++)
                {
                    for (int col = 0; col < 4; col++)
                    {
                        int px = bx * 4 + col;
                        int py = by * 4 + row;
                        int src = (py * texWidth + px) * 4;
                        int dst = (row * 4 + col) * 4;
                        block[dst] = rgba[src];
                        block[dst + 1] = rgba[src + 1];
                        block[dst + 2] = rgba[src + 2];
                        block[dst + 3] = rgba[src + 3];
                        if (rgba[src + 3] < 128)
                            hasAlpha = true;
                    }
                }

                Bc1EncodeBlock(block, hasAlpha, output, (by * blocksX + bx) * 8);
            }
        }

        return output;
    }

    private static void Bc1EncodeBlock(byte[] block, bool hasAlpha, byte[] output, int outOffset)
    {
        int minR = 255, minG = 255, minB = 255;
        int maxR = 0, maxG = 0, maxB = 0;
        int opaqueCount = 0;

        for (int i = 0; i < 16; i++)
        {
            int off = i * 4;
            if (block[off + 3] < 128)
                continue;

            opaqueCount++;
            int r = block[off], g = block[off + 1], b = block[off + 2];
            if (r < minR) minR = r;
            if (g < minG) minG = g;
            if (b < minB) minB = b;
            if (r > maxR) maxR = r;
            if (g > maxG) maxG = g;
            if (b > maxB) maxB = b;
        }

        if (opaqueCount == 0)
        {
            output[outOffset] = 0;
            output[outOffset + 1] = 0;
            output[outOffset + 2] = 0;
            output[outOffset + 3] = 0;
            output[outOffset + 4] = 0xFF;
            output[outOffset + 5] = 0xFF;
            output[outOffset + 6] = 0xFF;
            output[outOffset + 7] = 0xFF;
            return;
        }

        ushort c0 = Rgb565Encode((byte)maxR, (byte)maxG, (byte)maxB);
        ushort c1 = Rgb565Encode((byte)minR, (byte)minG, (byte)minB);

        if (hasAlpha)
        {
            if (c0 > c1) (c0, c1) = (c1, c0);
        }
        else
        {
            if (c0 < c1) (c0, c1) = (c1, c0);
            if (c0 == c1)
            {
                if (c0 < 0xFFFF) c0++;
                else c1--;
            }
        }

        var (r0, g0, b0) = Rgb565Decode(c0);
        var (r1, g1, b1) = Rgb565Decode(c1);

        int pr2, pg2, pb2, pr3, pg3, pb3;
        bool idx3IsTransparent;

        if (c0 > c1)
        {
            pr2 = (2 * r0 + r1) / 3; pg2 = (2 * g0 + g1) / 3; pb2 = (2 * b0 + b1) / 3;
            pr3 = (r0 + 2 * r1) / 3; pg3 = (g0 + 2 * g1) / 3; pb3 = (b0 + 2 * b1) / 3;
            idx3IsTransparent = false;
        }
        else
        {
            pr2 = (r0 + r1) / 2; pg2 = (g0 + g1) / 2; pb2 = (b0 + b1) / 2;
            pr3 = 0; pg3 = 0; pb3 = 0;
            idx3IsTransparent = true;
        }

        uint indices = 0;
        for (int i = 0; i < 16; i++)
        {
            int off = i * 4;
            int r = block[off], g = block[off + 1], b = block[off + 2], a = block[off + 3];

            int bestIdx;
            if (a < 128 && idx3IsTransparent)
            {
                bestIdx = 3;
            }
            else
            {
                int d0 = ColorDistSq(r, g, b, r0, g0, b0);
                int d1 = ColorDistSq(r, g, b, r1, g1, b1);
                int d2 = ColorDistSq(r, g, b, pr2, pg2, pb2);

                bestIdx = 0;
                int bestDist = d0;
                if (d1 < bestDist) { bestDist = d1; bestIdx = 1; }
                if (d2 < bestDist) { bestDist = d2; bestIdx = 2; }

                if (!idx3IsTransparent)
                {
                    int d3 = ColorDistSq(r, g, b, pr3, pg3, pb3);
                    if (d3 < bestDist) { bestIdx = 3; }
                }
            }

            indices |= (uint)(bestIdx << (2 * i));
        }

        BitConverter.TryWriteBytes(output.AsSpan(outOffset), c0);
        BitConverter.TryWriteBytes(output.AsSpan(outOffset + 2), c1);
        BitConverter.TryWriteBytes(output.AsSpan(outOffset + 4), indices);
    }

    private static int ColorDistSq(int r1, int g1, int b1, int r2, int g2, int b2)
    {
        int dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        return dr * dr + dg * dg + db * db;
    }
    private static byte[] Bc3Decode(byte[] blockData, int texWidth, int texHeight)
    {
        int blocksX = texWidth / 4;
        int blocksY = texHeight / 4;
        byte[] output = new byte[texWidth * texHeight * 4];
        byte[] alphas = new byte[8];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int blockOffset = (by * blocksX + bx) * 16;

                byte a0 = blockData[blockOffset];
                byte a1 = blockData[blockOffset + 1];

                ulong alphaIdxBits = 0;
                for (int i = 0; i < 6; i++)
                {
                    alphaIdxBits |= (ulong)blockData[blockOffset + 2 + i] << (8 * i);
                }

                alphas[0] = a0;
                alphas[1] = a1;
                if (a0 > a1)
                {
                    alphas[2] = (byte)((6 * a0 + 1 * a1) / 7);
                    alphas[3] = (byte)((5 * a0 + 2 * a1) / 7);
                    alphas[4] = (byte)((4 * a0 + 3 * a1) / 7);
                    alphas[5] = (byte)((3 * a0 + 4 * a1) / 7);
                    alphas[6] = (byte)((2 * a0 + 5 * a1) / 7);
                    alphas[7] = (byte)((1 * a0 + 6 * a1) / 7);
                }
                else
                {
                    alphas[2] = (byte)((4 * a0 + 1 * a1) / 5);
                    alphas[3] = (byte)((3 * a0 + 2 * a1) / 5);
                    alphas[4] = (byte)((2 * a0 + 3 * a1) / 5);
                    alphas[5] = (byte)((1 * a0 + 4 * a1) / 5);
                    alphas[6] = 0;
                    alphas[7] = 255;
                }

                ushort c0Raw = BitConverter.ToUInt16(blockData, blockOffset + 8);
                ushort c1Raw = BitConverter.ToUInt16(blockData, blockOffset + 10);
                uint colorIndices = BitConverter.ToUInt32(blockData, blockOffset + 12);

                var (r0, g0, b0) = Rgb565Decode(c0Raw);
                var (r1, g1, b1) = Rgb565Decode(c1Raw);

                byte pr0 = r0, pg0 = g0, pb0 = b0;
                byte pr1 = r1, pg1 = g1, pb1 = b1;
                byte pr2 = (byte)((2 * r0 + r1) / 3);
                byte pg2 = (byte)((2 * g0 + g1) / 3);
                byte pb2 = (byte)((2 * b0 + b1) / 3);
                byte pr3 = (byte)((r0 + 2 * r1) / 3);
                byte pg3 = (byte)((g0 + 2 * g1) / 3);
                byte pb3 = (byte)((b0 + 2 * b1) / 3);

                for (int row = 0; row < 4; row++)
                {
                    for (int col = 0; col < 4; col++)
                    {
                        int pixelIndex = row * 4 + col;
                        int ci = (int)((colorIndices >> (2 * pixelIndex)) & 0x3);
                        int ai = (int)((alphaIdxBits >> (3 * pixelIndex)) & 0x7);

                        int px = bx * 4 + col;
                        int py = by * 4 + row;
                        int dst = (py * texWidth + px) * 4;

                        byte r, g, b;
                        switch (ci)
                        {
                            case 0: r = pr0; g = pg0; b = pb0; break;
                            case 1: r = pr1; g = pg1; b = pb1; break;
                            case 2: r = pr2; g = pg2; b = pb2; break;
                            default: r = pr3; g = pg3; b = pb3; break;
                        }

                        output[dst] = r;
                        output[dst + 1] = g;
                        output[dst + 2] = b;
                        output[dst + 3] = alphas[ai];
                    }
                }
            }
        }

        return output;
    }

    private static byte[] Bc3Encode(byte[] rgba, int texWidth, int texHeight)
    {
        int blocksX = texWidth / 4;
        int blocksY = texHeight / 4;
        byte[] output = new byte[blocksX * blocksY * 16];
        byte[] block = new byte[64];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                for (int row = 0; row < 4; row++)
                {
                    for (int col = 0; col < 4; col++)
                    {
                        int px = bx * 4 + col;
                        int py = by * 4 + row;
                        int src = (py * texWidth + px) * 4;
                        int dst = (row * 4 + col) * 4;
                        block[dst] = rgba[src];
                        block[dst + 1] = rgba[src + 1];
                        block[dst + 2] = rgba[src + 2];
                        block[dst + 3] = rgba[src + 3];
                    }
                }

                Bc3EncodeBlock(block, output, (by * blocksX + bx) * 16);
            }
        }

        return output;
    }

    private static void Bc3EncodeBlock(byte[] block, byte[] output, int outOffset)
    {
        int minA = 255, maxA = 0;
        for (int i = 0; i < 16; i++)
        {
            int a = block[i * 4 + 3];
            if (a < minA) minA = a;
            if (a > maxA) maxA = a;
        }

        byte a0, a1;
        if (minA == maxA)
        {
            a0 = (byte)maxA;
            a1 = (byte)maxA;
        }
        else
        {
            a0 = (byte)maxA;
            a1 = (byte)minA;
        }

        output[outOffset] = a0;
        output[outOffset + 1] = a1;

        int[] alphaPal = new int[8];
        alphaPal[0] = a0;
        alphaPal[1] = a1;
        if (a0 > a1)
        {
            alphaPal[2] = (6 * a0 + 1 * a1) / 7;
            alphaPal[3] = (5 * a0 + 2 * a1) / 7;
            alphaPal[4] = (4 * a0 + 3 * a1) / 7;
            alphaPal[5] = (3 * a0 + 4 * a1) / 7;
            alphaPal[6] = (2 * a0 + 5 * a1) / 7;
            alphaPal[7] = (1 * a0 + 6 * a1) / 7;
        }
        else
        {
            alphaPal[2] = a0;
            alphaPal[3] = a0;
            alphaPal[4] = a0;
            alphaPal[5] = a0;
            alphaPal[6] = 0;
            alphaPal[7] = 255;
        }

        ulong alphaIdxBits = 0;
        for (int i = 0; i < 16; i++)
        {
            int a = block[i * 4 + 3];
            int bestIdx = 0;
            int bestDist = Math.Abs(a - alphaPal[0]);
            for (int p = 1; p < 8; p++)
            {
                int d = Math.Abs(a - alphaPal[p]);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = p;
                }
            }
            alphaIdxBits |= ((ulong)bestIdx) << (3 * i);
        }

        for (int i = 0; i < 6; i++)
        {
            output[outOffset + 2 + i] = (byte)((alphaIdxBits >> (8 * i)) & 0xFF);
        }

        int minR = 255, minG = 255, minB = 255;
        int maxR = 0, maxG = 0, maxB = 0;

        for (int i = 0; i < 16; i++)
        {
            int off = i * 4;
            int r = block[off], g = block[off + 1], b = block[off + 2];
            if (r < minR) minR = r;
            if (g < minG) minG = g;
            if (b < minB) minB = b;
            if (r > maxR) maxR = r;
            if (g > maxG) maxG = g;
            if (b > maxB) maxB = b;
        }

        ushort c0 = Rgb565Encode((byte)maxR, (byte)maxG, (byte)maxB);
        ushort c1 = Rgb565Encode((byte)minR, (byte)minG, (byte)minB);

        if (c0 < c1) (c0, c1) = (c1, c0);
        if (c0 == c1)
        {
            if (c0 < 0xFFFF) c0++;
            else c1--;
        }

        var (r0, g0, b0) = Rgb565Decode(c0);
        var (r1, g1, b1) = Rgb565Decode(c1);

        int pr2 = (2 * r0 + r1) / 3, pg2 = (2 * g0 + g1) / 3, pb2 = (2 * b0 + b1) / 3;
        int pr3 = (r0 + 2 * r1) / 3, pg3 = (g0 + 2 * g1) / 3, pb3 = (b0 + 2 * b1) / 3;

        uint colorIndices = 0;
        for (int i = 0; i < 16; i++)
        {
            int off = i * 4;
            int r = block[off], g = block[off + 1], b = block[off + 2];

            int d0 = ColorDistSq(r, g, b, r0, g0, b0);
            int d1 = ColorDistSq(r, g, b, r1, g1, b1);
            int d2 = ColorDistSq(r, g, b, pr2, pg2, pb2);
            int d3 = ColorDistSq(r, g, b, pr3, pg3, pb3);

            int bestIdx = 0;
            int bestDist = d0;
            if (d1 < bestDist) { bestDist = d1; bestIdx = 1; }
            if (d2 < bestDist) { bestDist = d2; bestIdx = 2; }
            if (d3 < bestDist) { bestIdx = 3; }

            colorIndices |= (uint)(bestIdx << (2 * i));
        }

        BitConverter.TryWriteBytes(output.AsSpan(outOffset + 8), c0);
        BitConverter.TryWriteBytes(output.AsSpan(outOffset + 10), c1);
        BitConverter.TryWriteBytes(output.AsSpan(outOffset + 12), colorIndices);
    }
}