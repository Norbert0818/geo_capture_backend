namespace GeoCapture.Api.Models;

public sealed class CapturedPhoto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UploadId { get; set; }
    public PhotoUpload Upload { get; set; } = null!;
    public Guid ClientPhotoId { get; set; }
    public int Position { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AccuracyMeters { get; set; }
    public string CoordinatesText { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; }
    public DateTime LocationMeasuredAtUtc { get; set; }
    public string OriginalMimeType { get; set; } = "";
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
    public int StampedWidth { get; set; }
    public int StampedHeight { get; set; }
    public long OriginalByteLength { get; set; }
    public long StampedByteLength { get; set; }
    public string OriginalSha256 { get; set; } = "";
    public string StampedSha256 { get; set; } = "";
    public PhotoContent Content { get; set; } = null!;
}