using System.Buffers.Binary;
using SkiaSharp;

namespace MMCA.Common.Infrastructure.Tests.Storage;

/// <summary>
/// Builds the inputs for <see cref="SkiaSharpImageProcessorTests"/>. JPEG, PNG and WebP are encoded
/// with SkiaSharp; GIF, BMP, ICO and TIFF are written byte by byte because SkiaSharp cannot encode
/// them; the metadata and declared-size fixtures are byte-level rewrites of SkiaSharp output.
/// </summary>
internal static class ImageFixtures
{
    /// <summary>GIF palette index of <see cref="Red"/>.</summary>
    public const byte GifRed = 0;

    /// <summary>GIF palette index of <see cref="Blue"/>.</summary>
    public const byte GifBlue = 1;

    /// <summary>GIF palette index of <see cref="Solid"/>.</summary>
    public const byte GifSolid = 3;

    /// <summary>EXIF orientation 1: stored layout is the display layout.</summary>
    public const ushort OrientationTopLeft = 1;

    /// <summary>EXIF orientation 6: rotate 90 degrees clockwise to display.</summary>
    public const ushort OrientationRightTop = 6;

    public static readonly SKColor Red = new(255, 0, 0);

    public static readonly SKColor Blue = new(0, 0, 255);

    public static readonly SKColor Green = new(0, 255, 0);

    public static readonly SKColor Solid = new(30, 120, 200);

    private static readonly SKColor[] GifPalette = [Red, Blue, Green, Solid];

    /// <summary>Encodes a <paramref name="width"/> x <paramref name="height"/> image filled with one color.</summary>
    public static byte[] CreateSolid(SKEncodedImageFormat format, int width, int height, SKColor color) =>
        Create(format, width, height, color);

    /// <summary>
    /// Encodes an image filled with <paramref name="background"/>, then with each rectangle painted
    /// in its color (left, top, right, bottom in pixels, right and bottom exclusive).
    /// </summary>
    public static byte[] Create(
        SKEncodedImageFormat format,
        int width,
        int height,
        SKColor background,
        params (SKRect Rect, SKColor Color)[] fills)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(background);
            foreach (var (rect, color) in fills)
            {
                using var paint = new SKPaint { Color = color, IsAntialias = false };
                canvas.DrawRect(rect, paint);
            }
        }

        return Encode(bitmap, format);
    }

    /// <summary>
    /// Encodes a JPEG of busy, non-repeating content, so the entropy-coded scan dominates the file
    /// and cutting the file in half leaves every header intact but the scan incomplete.
    /// </summary>
    public static byte[] CreateBusyJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var pixels = new SKColor[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new SKColor((byte)(x * 7 + y * 13), (byte)(x * x + y), (byte)(x ^ y * 3));
            }
        }

        bitmap.Pixels = pixels;
        return Encode(bitmap, SKEncodedImageFormat.Jpeg);
    }

    /// <summary>
    /// Encodes a tiny PNG, then rewrites the IHDR width and height (and the chunk CRC) so the header
    /// declares a frame the file never contains: a decompression bomb that costs nothing to build.
    /// </summary>
    public static byte[] CreatePngWithDeclaredSize(int width, int height)
    {
        var bytes = CreateSolid(SKEncodedImageFormat.Png, 4, 4, Solid);

        // PNG: 8-byte signature, then IHDR: length (4), type "IHDR" (4), data (13, starting with
        // width and height as big-endian uint32), CRC (4) over type plus data.
        const int ihdrTypeOffset = 12;
        const int ihdrDataOffset = 16;
        const int ihdrCrcOffset = 29;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(ihdrDataOffset, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(ihdrDataOffset + 4, 4), (uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(ihdrCrcOffset, 4), Crc32(bytes.AsSpan(ihdrTypeOffset, 17)));
        return bytes;
    }

    /// <summary>
    /// Writes a GIF89a with one full-canvas frame per entry of <paramref name="frameColorIndexes"/>,
    /// each frame a single palette color.
    /// </summary>
    public static byte[] CreateGif(int width, int height, params byte[] frameColorIndexes)
    {
        using var gif = new MemoryStream();
        gif.Write("GIF89a"u8);

        // Logical screen descriptor: global color table present, 8-bit color resolution,
        // 2^(6+1) = 128 palette entries (matching the 7-bit LZW minimum code size below).
        WriteUInt16LittleEndian(gif, width);
        WriteUInt16LittleEndian(gif, height);
        gif.Write([0xF6, 0x00, 0x00]);
        for (var i = 0; i < 128; i++)
        {
            var color = i < GifPalette.Length ? GifPalette[i] : SKColors.Black;
            gif.Write([color.Red, color.Green, color.Blue]);
        }

        if (frameColorIndexes.Length > 1)
        {
            // NETSCAPE2.0 application extension: loop forever.
            gif.Write([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8, 0x03, 0x01, 0x00, 0x00, 0x00]);
        }

        foreach (var colorIndex in frameColorIndexes)
        {
            // Graphic control extension: disposal "do not dispose", 100 ms delay, no transparency.
            gif.Write([0x21, 0xF9, 0x04, 0x04, 0x0A, 0x00, 0x00, 0x00]);

            // Image descriptor at (0, 0), full canvas, no local color table, not interlaced.
            gif.WriteByte(0x2C);
            WriteUInt16LittleEndian(gif, 0);
            WriteUInt16LittleEndian(gif, 0);
            WriteUInt16LittleEndian(gif, width);
            WriteUInt16LittleEndian(gif, height);
            gif.WriteByte(0x00);

            WriteUncompressedLzw(gif, width * height, colorIndex);
        }

        gif.WriteByte(0x3B);
        return gif.ToArray();
    }

    /// <summary>Writes an uncompressed 24-bit bottom-up BMP (BITMAPINFOHEADER) of one color.</summary>
    public static byte[] CreateBmp(int width, int height, SKColor color)
    {
        const int headerSize = 14 + 40;
        var rowSize = (width * 3 + 3) & ~3;
        var pixelBytes = rowSize * height;
        var bytes = new byte[headerSize + pixelBytes];
        var span = bytes.AsSpan();

        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], width);
        BinaryPrimitives.WriteInt32LittleEndian(span[22..], height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], 24);
        BinaryPrimitives.WriteInt32LittleEndian(span[30..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[34..], pixelBytes);
        BinaryPrimitives.WriteInt32LittleEndian(span[38..], 2835);
        BinaryPrimitives.WriteInt32LittleEndian(span[42..], 2835);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = headerSize + y * rowSize + x * 3;
                span[offset] = color.Blue;
                span[offset + 1] = color.Green;
                span[offset + 2] = color.Red;
            }
        }

        return bytes;
    }

    /// <summary>Writes a single-image ICO whose image is an embedded PNG (the Vista ICO layout).</summary>
    public static byte[] CreateIco(int size, SKColor color)
    {
        var png = CreateSolid(SKEncodedImageFormat.Png, size, size, color);
        const int imageOffset = 6 + 16;
        var bytes = new byte[imageOffset + png.Length];
        var span = bytes.AsSpan();

        // ICONDIR: reserved, type 1 (icon), one image.
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1);

        // ICONDIRENTRY: width, height (0 means 256), no palette, 1 plane, 32 bpp, size, offset.
        span[6] = (byte)(size % 256);
        span[7] = (byte)(size % 256);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], png.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], imageOffset);
        png.CopyTo(span[imageOffset..]);
        return bytes;
    }

    /// <summary>Writes an uncompressed little-endian 8-bit grayscale baseline TIFF, one strip.</summary>
    public static byte[] CreateTiff(int width, int height, byte gray)
    {
        const int entryCount = 8;
        const int ifdOffset = 8;
        const int dataOffset = ifdOffset + 2 + entryCount * 12 + 4;
        var bytes = new byte[dataOffset + width * height];
        var span = bytes.AsSpan();

        span[0] = (byte)'I';
        span[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], ifdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(span[ifdOffset..], entryCount);

        const ushort shortType = 3;
        const ushort longType = 4;
        var entry = ifdOffset + 2;
        WriteTiffEntry(span, ref entry, 256, shortType, (uint)width); // ImageWidth
        WriteTiffEntry(span, ref entry, 257, shortType, (uint)height); // ImageLength
        WriteTiffEntry(span, ref entry, 258, shortType, 8); // BitsPerSample
        WriteTiffEntry(span, ref entry, 259, shortType, 1); // Compression: none
        WriteTiffEntry(span, ref entry, 262, shortType, 1); // PhotometricInterpretation: BlackIsZero
        WriteTiffEntry(span, ref entry, 273, longType, dataOffset); // StripOffsets
        WriteTiffEntry(span, ref entry, 278, shortType, (uint)height); // RowsPerStrip
        WriteTiffEntry(span, ref entry, 279, longType, (uint)(width * height)); // StripByteCounts

        // Next-IFD offset 0 (already zero), then the pixels.
        span[dataOffset..].Fill(gray);
        return bytes;
    }

    /// <summary>
    /// Builds a JPEG APP1 Exif segment (big-endian TIFF body) holding IFD0 with the Orientation tag
    /// and a GPS IFD pointer, and a GPS IFD with GPSVersionID, GPSLatitudeRef and GPSLatitude.
    /// </summary>
    public static byte[] CreateExifSegment(ushort orientation)
    {
        var tiff = new byte[104];
        var span = tiff.AsSpan();
        span[0] = (byte)'M';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 42);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], 8);

        // IFD0 at 8: Orientation (SHORT, left-justified in the value field) and the GPS IFD pointer.
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], 2);
        WriteExifEntry(span[10..], 0x0112, 3, 1, (uint)orientation << 16);
        WriteExifEntry(span[22..], 0x8825, 4, 1, 38);
        BinaryPrimitives.WriteUInt32BigEndian(span[34..], 0);

        // GPS IFD at 38: version 2.2.0.0, latitude "N" 33 deg 45 min 0 sec (rationals at 80).
        BinaryPrimitives.WriteUInt16BigEndian(span[38..], 3);
        WriteExifEntry(span[40..], 0x0000, 1, 4, 0x02020000);
        WriteExifEntry(span[52..], 0x0001, 2, 2, (uint)'N' << 24);
        WriteExifEntry(span[64..], 0x0002, 5, 3, 80);
        BinaryPrimitives.WriteUInt32BigEndian(span[76..], 0);
        uint[] latitude = [33, 1, 45, 1, 0, 1];
        for (var i = 0; i < latitude.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span[(80 + i * 4)..], latitude[i]);
        }

        return CreateSegment(0xE1, [.. "Exif\0\0"u8, .. tiff]);
    }

    /// <summary>Builds a JPEG APP1 XMP segment carrying a minimal xpacket with a creator.</summary>
    public static byte[] CreateXmpSegment() =>
        CreateSegment(
            0xE1,
            [
                .. "http://ns.adobe.com/xap/1.0/\0"u8,
                .. "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/' dc:creator='Test Artist'/></rdf:RDF></x:xmpmeta><?xpacket end='w'?>"u8,
            ]);

    /// <summary>
    /// Builds a JPEG APP13 Photoshop segment holding one 8BIM 0x0404 (IPTC-NAA) resource with the
    /// record version and a By-line dataset.
    /// </summary>
    public static byte[] CreateIptcSegment()
    {
        byte[] iptc = [0x1C, 0x02, 0x00, 0x00, 0x02, 0x00, 0x04, 0x1C, 0x02, 0x50, 0x00, 0x0B, .. "Test Artist"u8];
        var paddedLength = iptc.Length + iptc.Length % 2;
        var resource = new byte[4 + 2 + 2 + 4 + paddedLength];
        "8BIM"u8.CopyTo(resource);
        BinaryPrimitives.WriteUInt16BigEndian(resource.AsSpan(4), 0x0404);

        // Empty Pascal-string name padded to even length (two zero bytes at 6), then the data size.
        BinaryPrimitives.WriteInt32BigEndian(resource.AsSpan(8), iptc.Length);
        iptc.CopyTo(resource.AsSpan(12));
        return CreateSegment(0xED, [.. "Photoshop 3.0\0"u8, .. resource]);
    }

    /// <summary>Inserts the given marker segments immediately after the JPEG SOI marker.</summary>
    public static byte[] InsertAfterSoi(byte[] jpeg, params byte[][] segments)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new ArgumentException("Not a JPEG: the SOI marker is missing.", nameof(jpeg));
        }

        using var output = new MemoryStream();
        output.Write(jpeg, 0, 2);
        foreach (var segment in segments)
        {
            output.Write(segment);
        }

        output.Write(jpeg, 2, jpeg.Length - 2);
        return output.ToArray();
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }

    /// <summary>
    /// Writes GIF image data with a 7-bit LZW minimum code size, so every code is exactly 8 bits
    /// (one byte), emitting only literal codes and a clear code every 100 literals. The decoder's
    /// table then never grows to 256 entries, so the code width never changes.
    /// </summary>
    private static void WriteUncompressedLzw(Stream gif, int pixelCount, byte colorIndex)
    {
        const byte minimumCodeSize = 7;
        const byte clearCode = 0x80;
        const byte endCode = 0x81;
        const int literalsPerClear = 100;

        var codes = new byte[pixelCount + (pixelCount + literalsPerClear - 1) / literalsPerClear + 1];
        var next = 0;
        for (var i = 0; i < pixelCount; i++)
        {
            if (i % literalsPerClear == 0)
            {
                codes[next++] = clearCode;
            }

            codes[next++] = colorIndex;
        }

        codes[next++] = endCode;

        gif.WriteByte(minimumCodeSize);
        for (var offset = 0; offset < next; offset += 255)
        {
            var length = Math.Min(255, next - offset);
            gif.WriteByte((byte)length);
            gif.Write(codes, offset, length);
        }

        gif.WriteByte(0x00);
    }

    private static void WriteUInt16LittleEndian(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)value);
        stream.Write(buffer);
    }

    private static void WriteTiffEntry(Span<byte> span, ref int offset, ushort tag, ushort type, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], tag);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 2)..], type);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], 1);
        if (type == 3)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 8)..], (ushort)value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 8)..], value);
        }

        offset += 12;
    }

    private static void WriteExifEntry(Span<byte> entry, ushort tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(entry, tag);
        BinaryPrimitives.WriteUInt16BigEndian(entry[2..], type);
        BinaryPrimitives.WriteUInt32BigEndian(entry[4..], count);
        BinaryPrimitives.WriteUInt32BigEndian(entry[8..], value);
    }

    private static byte[] CreateSegment(byte marker, ReadOnlySpan<byte> payload)
    {
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment.AsSpan(4));
        return segment;
    }

    /// <summary>CRC-32 (ISO-HDLC, the PNG chunk CRC), bitwise; fixtures are tiny.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
