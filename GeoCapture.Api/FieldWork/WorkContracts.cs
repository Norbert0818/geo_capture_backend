namespace GeoCapture.Api.FieldWork;

public sealed record PoleIdentity(string SerialNumber, string NtetCode, string Source, string? RawCode);
public sealed record WorkPhotoDescriptor(Guid Id, string Role, double Latitude, double Longitude,
    double AccuracyMeters, DateTime CapturedAtUtc, DateTime MeasuredAtUtc,
    int UtcOffsetMinutes, string Locality, string County, string PlaceSource,
    bool SerialVisible, string OriginalMimeType, string OverlayText);
public sealed record WorkDocument(string Kind, PoleIdentity? OldPole, PoleIdentity? NewPole,
    string Notes, WorkPhotoDescriptor[] Photos);
public sealed record WorkSaveRequest(Guid ChangeId, Guid? ExpectedVersion, string Reason, WorkDocument Document);
public sealed record WorkDetails(Guid Id, Guid OwnerId, string Owner, int Revision,
    Guid Version, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, WorkDocument Document, bool Replayed = false);
public sealed record WorkSummary(Guid Id, string Owner, string Kind, string? SerialNumber,
    int Revision, DateTime UpdatedAtUtc, int PhotoCount);
public sealed record WorkHistory(int Revision, string Actor, DateTime AtUtc, string Reason,
    string[] Changes, WorkDocument Document);
public sealed record WorkSettingsResponse(bool AllowQrGeneration, Guid Version);
public sealed record WorkSettingsRequest(bool AllowQrGeneration, Guid ExpectedVersion);

public sealed class WorkException(string code, int status = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}