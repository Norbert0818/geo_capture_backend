namespace GeoCapture.Api.Models;

public sealed class PhotoUpload
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientUploadId { get; set; }
    public Guid OwnerId { get; set; }
    public AppUser Owner { get; set; } = null!;
    public string UploadedByUsername { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string? OldSerialNumber { get; set; }
    public string PayloadSha256 { get; set; } = "";
    public DateTime UploadedAtUtc { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public List<CapturedPhoto> Photos { get; set; } = [];
}