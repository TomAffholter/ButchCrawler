using System.Buffers.Binary;

namespace ButchCrawler.App;

public static class ImageSecurityValidator
{
    public static void Validate(byte[] data, string? extension, long maxPixels)
    {
        if (data.Length < 16) throw new InvalidDataException("Image is too small to contain valid dimensions.");
        var ext = (extension ?? string.Empty).ToLowerInvariant();
        (long width, long height, long pages) dimensions = ext switch
        {
            ".png" => ReadPng(data),
            ".jpg" or ".jpeg" => ReadJpeg(data),
            ".bmp" => ReadBmp(data),
            ".tif" or ".tiff" => ReadTiff(data),
            ".webp" => ReadWebP(data),
            _ => throw new InvalidDataException("Image type is not approved for OCR.")
        };
        if (dimensions.width <= 0 || dimensions.height <= 0 || dimensions.pages <= 0 ||
            dimensions.width > 100_000 || dimensions.height > 100_000 ||
            dimensions.width * dimensions.height * dimensions.pages > maxPixels)
            throw new InvalidDataException($"Image exceeds the secure {maxPixels:N0}-pixel processing limit.");
    }

    private static (long, long, long) ReadPng(byte[] d)
    {
        if (d.Length < 24 || !d.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Invalid PNG header.");
        return (BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(16, 4)), BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(20, 4)), 1);
    }

    private static (long, long, long) ReadBmp(byte[] d)
    {
        if (d.Length < 26 || d[0] != (byte)'B' || d[1] != (byte)'M') throw new InvalidDataException("Invalid BMP header.");
        return (Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(18, 4))),
            Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(22, 4))), 1);
    }

    private static (long, long, long) ReadJpeg(byte[] d)
    {
        if (d[0] != 0xFF || d[1] != 0xD8) throw new InvalidDataException("Invalid JPEG header.");
        var i = 2;
        while (i + 8 < d.Length)
        {
            if (d[i++] != 0xFF) continue;
            while (i < d.Length && d[i] == 0xFF) i++;
            if (i >= d.Length) break;
            var marker = d[i++];
            if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7) continue;
            if (i + 2 > d.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i, 2));
            if (length < 2 || i + length > d.Length) break;
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (length < 7) break;
                return (BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 5, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 3, 2)), 1);
            }
            i += length;
        }
        throw new InvalidDataException("JPEG dimensions could not be validated.");
    }

    private static (long, long, long) ReadTiff(byte[] d)
    {
        var little = d[0] == (byte)'I' && d[1] == (byte)'I';
        if (!little && !(d[0] == (byte)'M' && d[1] == (byte)'M')) throw new InvalidDataException("Invalid TIFF header.");
        ushort U16(int p) => little ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p, 2)) : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p, 2));
        uint U32(int p) => little ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4)) : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p, 4));
        if (U16(2) != 42) throw new InvalidDataException("Unsupported TIFF format.");
        var offset = (long)U32(4); long pages = 0, totalPixels = 0, firstWidth = 0, firstHeight = 0;
        var visited = new HashSet<long>();
        while (offset != 0)
        {
            if (++pages > 500 || !visited.Add(offset) || offset < 8 || offset + 2 > d.Length) throw new InvalidDataException("TIFF page structure exceeds secure limits.");
            var count = U16((int)offset); var entriesStart = offset + 2;
            if (entriesStart + count * 12L + 4 > d.Length) throw new InvalidDataException("Invalid TIFF directory.");
            long width = 0, height = 0;
            for (var n = 0; n < count; n++)
            {
                var p = checked((int)(entriesStart + n * 12L)); var tag = U16(p); var type = U16(p + 2); var itemCount = U32(p + 4);
                if (itemCount != 1 || tag is not (256 or 257)) continue;
                long value = type == 3 ? U16(p + 8) : type == 4 ? U32(p + 8) : 0;
                if (tag == 256) width = value; else height = value;
            }
            if (width <= 0 || height <= 0) throw new InvalidDataException("TIFF dimensions could not be validated.");
            if (pages == 1) { firstWidth = width; firstHeight = height; }
            totalPixels = checked(totalPixels + checked(width * height));
            offset = U32(checked((int)(entriesStart + count * 12L)));
        }
        var firstPagePixels = Math.Max(1, firstWidth * firstHeight);
        return (firstWidth, firstHeight, Math.Max(1, (totalPixels + firstPagePixels - 1) / firstPagePixels));
    }

    private static (long, long, long) ReadWebP(byte[] d)
    {
        if (d.Length < 30 || !d.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !d.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            throw new InvalidDataException("Invalid WebP header.");
        var kind = System.Text.Encoding.ASCII.GetString(d, 12, 4);
        if (kind == "VP8X")
        {
            var width = 1L + d[24] + (d[25] << 8) + (d[26] << 16);
            var height = 1L + d[27] + (d[28] << 8) + (d[29] << 16);
            return (width, height, 1);
        }
        if (kind == "VP8L" && d[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(21, 4));
            return ((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1, 1);
        }
        if (kind == "VP8 " && d[23] == 0x9D && d[24] == 0x01 && d[25] == 0x2A)
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(26, 2)) & 0x3FFF;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(28, 2)) & 0x3FFF;
            return (width, height, 1);
        }
        throw new InvalidDataException("WebP dimensions could not be validated.");
    }
}
