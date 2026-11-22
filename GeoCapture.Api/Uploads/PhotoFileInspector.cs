using System.Buffers.Binary;

namespace GeoCapture.Api.Uploads;

public sealed record PhotoFileInfo(string MimeType, int Width, int Height);

public static class PhotoFileInspector
{
    // This checks format headers and dimensions, not the complete compressed image.
    public static PhotoFileInfo Inspect(byte[] bytes, long maxPixels)
    {
        ReadOnlySpan<byte> data = bytes;
        ReadOnlySpan<byte> pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        ReadOnlySpan<byte> pngEnd = [0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130];

        if (data.StartsWith(pngSignature))
        {
            if (data.Length < 45 ||
                BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4)) != 13 ||
                !data.Slice(12, 4).SequenceEqual("IHDR"u8) ||
                !data.EndsWith(pngEnd))
            {
                throw new UploadException("invalid_photo");
            }

            var width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
            return Validate("image/png", width, height, maxPixels);
        }

        if (data.Length < 4 || data[0] != 255 || data[1] != 216 ||
            data[^2] != 255 || data[^1] != 217)
        {
            throw new UploadException("invalid_photo");
        }

        var offset = 2;
        while (offset < data.Length - 2)
        {
            if (data[offset++] != 255) throw new UploadException("invalid_photo");
            while (offset < data.Length && data[offset] == 255) offset++;
            if (offset >= data.Length) break;
            var marker = data[offset++];

            if (marker is 0xDA or 0xD9) break;
            if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue;
            if (offset + 2 > data.Length) break;

            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            if (length < 2 || offset + length > data.Length)
            {
                throw new UploadException("invalid_photo");
            }

            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (length < 8) throw new UploadException("invalid_photo");
                var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2));
                return Validate("image/jpeg", width, height, maxPixels);
            }

            offset += length;
        }

        throw new UploadException("invalid_photo");
    }

    private static PhotoFileInfo Validate(string mime, uint width, uint height, long maxPixels)
    {
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue ||
            (long)width * height > maxPixels)
        {
            throw new UploadException("invalid_photo_dimensions");
        }

        return new PhotoFileInfo(mime, (int)width, (int)height);
    }
}