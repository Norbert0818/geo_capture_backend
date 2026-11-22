namespace GeoCapture.Api.Uploads;

public sealed class UploadOptions
{
    public int MaxPhotos { get; set; } = 10;
    public int MaxSerialNumberLength { get; set; } = 256;
    public long MaxFileBytes { get; set; } = 8 * 1024 * 1024;
    public long MaxBatchBytes { get; set; } = 50 * 1024 * 1024;
    public long MaxRequestBytes { get; set; } = 64 * 1024 * 1024;
    public long MaxImagePixels { get; set; } = 40_000_000;
}

public sealed record UploadManifest(
    Guid Id,
    string? SerialNumber,
    UploadPhotoMetadata?[]? Photos);

public sealed record UploadPhotoMetadata(
    Guid Id,
    double Latitude,
    double Longitude,
    double AccuracyMeters,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset LocationMeasuredAtUtc);

public sealed record UploadSummary(
    Guid Id,
    Guid ClientUploadId,
    string SerialNumber,
    Guid OwnerId,
    string UploadedByUsername,
    DateTime UploadedAtUtc,
    int PhotoCount,
    Guid Version);

public sealed record UploadPage(int Page, int PageSize, int Total, UploadSummary[] Items);

public sealed record UploadDetails(
    Guid Id,
    Guid ClientUploadId,
    string SerialNumber,
    Guid OwnerId,
    string UploadedByUsername,
    DateTime UploadedAtUtc,
    Guid Version,
    bool Replayed,
    UploadedPhotoResponse[] Photos);

public sealed record UploadedPhotoResponse(
    Guid Id,
    Guid ClientPhotoId,
    int Position,
    double Latitude,
    double Longitude,
    double AccuracyMeters,
    string CoordinatesText,
    DateTime CapturedAtUtc,
    DateTime LocationMeasuredAtUtc,
    string OriginalMimeType,
    int OriginalWidth,
    int OriginalHeight,
    int StampedWidth,
    int StampedHeight,
    long OriginalByteLength,
    long StampedByteLength,
    string OriginalSha256,
    string StampedSha256,
    string OriginalUrl,
    string StampedUrl);

public sealed class UploadException(string code, int statusCode = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}