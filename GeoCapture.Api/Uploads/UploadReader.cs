using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GeoCapture.Api.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace GeoCapture.Api.Uploads;

public sealed record PreparedUpload(Guid ClientUploadId, string SerialNumber,
    string PayloadSha256, CapturedPhoto[] Photos);

public sealed class UploadReader(IOptions<UploadOptions> options, TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true
    };

    public async Task<PreparedUpload> ReadAsync(HttpRequest request, CancellationToken token)
    {
        var limits = options.Value;
        if (!request.HasFormContentType || request.ContentType is null ||
            !request.ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new UploadException("multipart_required", 415);
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(new FormOptions
            {
                MultipartBodyLengthLimit = limits.MaxRequestBytes,
                ValueLengthLimit = 64 * 1024,
                ValueCountLimit = 1 + (2 * limits.MaxPhotos),
                MemoryBufferThreshold = 64 * 1024
            }, token);
        }
        catch (InvalidDataException)
        {
            throw new UploadException("invalid_upload_form");
        }

        if (form.Count != 1 || !form.TryGetValue("manifest", out var values) ||
            values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            throw new UploadException("invalid_manifest");
        }

        UploadManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UploadManifest>(values[0]!, JsonOptions);
        }
        catch (JsonException)
        {
            throw new UploadException("invalid_manifest");
        }

        if (manifest is null || manifest.Id == Guid.Empty || manifest.Photos is null ||
            manifest.Photos.Length < 1 || manifest.Photos.Length > limits.MaxPhotos ||
            manifest.Photos.Any(x => x is null))
        {
            throw new UploadException("invalid_manifest");
        }

        var serial = manifest.SerialNumber?.Trim();
        if (string.IsNullOrEmpty(serial) || serial.Length > limits.MaxSerialNumberLength ||
            serial.Any(char.IsControl))
        {
            throw new UploadException("invalid_serial_number");
        }

        var metadata = manifest.Photos.OfType<UploadPhotoMetadata>().ToArray();
        if (metadata.Any(x => x.Id == Guid.Empty) ||
            metadata.Select(x => x.Id).Distinct().Count() != metadata.Length)
        {
            throw new UploadException("duplicate_photo_id");
        }

        var now = clock.GetUtcNow();
        foreach (var photo in metadata) ValidateMetadata(photo, now);

        var files = new Dictionary<string, IFormFile>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var file in form.Files)
        {
            if (!files.TryAdd(file.Name, file)) throw new UploadException("duplicate_photo_file");
            if (file.Length <= 0) throw new UploadException("invalid_photo");
            if (file.Length > limits.MaxFileBytes) throw new UploadException("upload_too_large", 413);
            totalBytes += file.Length;
            if (totalBytes > limits.MaxBatchBytes) throw new UploadException("upload_too_large", 413);
        }

        if (files.Count != metadata.Length * 2) throw new UploadException("photo_pair_required");

        var photos = new List<CapturedPhoto>();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var normalizedManifest = new UploadManifest(manifest.Id, serial,
            metadata.Select(x => x with
            {
                CapturedAtUtc = x.CapturedAtUtc.ToUniversalTime(),
                LocationMeasuredAtUtc = x.LocationMeasuredAtUtc.ToUniversalTime()
            }).ToArray());
        digest.AppendData(JsonSerializer.SerializeToUtf8Bytes(normalizedManifest, JsonOptions));

        foreach (var photo in metadata)
        {
            if (!files.TryGetValue($"original_{photo.Id:D}", out var originalFile) ||
                !files.TryGetValue($"stamped_{photo.Id:D}", out var stampedFile))
            {
                throw new UploadException("photo_pair_required");
            }

            var original = await ReadBytes(originalFile, token);
            var stamped = await ReadBytes(stampedFile, token);
            var originalInfo = PhotoFileInspector.Inspect(original, limits.MaxImagePixels);
            var stampedInfo = PhotoFileInspector.Inspect(stamped, limits.MaxImagePixels);
            if (stampedInfo.MimeType != "image/jpeg")
            {
                throw new UploadException("stamped_jpeg_required");
            }

            var originalHash = SHA256.HashData(original);
            var stampedHash = SHA256.HashData(stamped);
            digest.AppendData(originalHash);
            digest.AppendData(stampedHash);

            photos.Add(new CapturedPhoto
            {
                ClientPhotoId = photo.Id,
                Position = photos.Count,
                Latitude = photo.Latitude,
                Longitude = photo.Longitude,
                AccuracyMeters = photo.AccuracyMeters,
                CoordinatesText = $"{Coordinate(photo.Latitude)}, {Coordinate(photo.Longitude)}",
                CapturedAtUtc = photo.CapturedAtUtc.UtcDateTime,
                LocationMeasuredAtUtc = photo.LocationMeasuredAtUtc.UtcDateTime,
                OriginalMimeType = originalInfo.MimeType,
                OriginalWidth = originalInfo.Width,
                OriginalHeight = originalInfo.Height,
                StampedWidth = stampedInfo.Width,
                StampedHeight = stampedInfo.Height,
                OriginalByteLength = original.LongLength,
                StampedByteLength = stamped.LongLength,
                OriginalSha256 = Convert.ToHexString(originalHash),
                StampedSha256 = Convert.ToHexString(stampedHash),
                Content = new PhotoContent { OriginalBytes = original, StampedBytes = stamped }
            });
        }

        return new PreparedUpload(manifest.Id, serial,
            Convert.ToHexString(digest.GetHashAndReset()), photos.ToArray());
    }

    private static async Task<byte[]> ReadBytes(IFormFile file, CancellationToken token)
    {
        using var stream = new MemoryStream((int)file.Length);
        await file.CopyToAsync(stream, token);
        return stream.ToArray();
    }

    private static void ValidateMetadata(UploadPhotoMetadata photo, DateTimeOffset now)
    {
        if (!double.IsFinite(photo.Latitude) || photo.Latitude is < -90 or > 90 ||
            !double.IsFinite(photo.Longitude) || photo.Longitude is < -180 or > 180 ||
            !double.IsFinite(photo.AccuracyMeters) || photo.AccuracyMeters < 0)
        {
            throw new UploadException("invalid_location");
        }

        if (photo.CapturedAtUtc < DateTimeOffset.UnixEpoch ||
            photo.LocationMeasuredAtUtc < DateTimeOffset.UnixEpoch ||
            photo.CapturedAtUtc > now.AddMinutes(5) ||
            photo.LocationMeasuredAtUtc > now.AddMinutes(5) ||
            (photo.CapturedAtUtc - photo.LocationMeasuredAtUtc).Duration() > TimeSpan.FromSeconds(30))
        {
            throw new UploadException("invalid_photo_time");
        }
    }

    private static string Coordinate(double value)
    {
        var text = value.ToString("F6", CultureInfo.InvariantCulture);
        return text == "-0.000000" ? "0.000000" : text;
    }
}