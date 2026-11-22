namespace GeoCapture.Api.Models;

public sealed class PhotoContent
{
    public Guid PhotoId { get; set; }
    public CapturedPhoto Photo { get; set; } = null!;
    public byte[] OriginalBytes { get; set; } = [];
    public byte[] StampedBytes { get; set; } = [];
}