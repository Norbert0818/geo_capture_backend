using System.Text.Json.Serialization;

namespace GeoCapture.Api.FieldWork;

public sealed record PoleIdentity(string SerialNumber, string NtetCode, string Source, string? RawCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SerialState = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Confirmed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? EvidencePhotoId = null);
public sealed record WorkPhotoDescriptor(Guid Id, string Role, double Latitude, double Longitude,
    double AccuracyMeters, DateTime CapturedAtUtc, DateTime MeasuredAtUtc,
    int UtcOffsetMinutes, string Locality, string County, string PlaceSource,
    bool SerialVisible, string OriginalMimeType, string OverlayText,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CountyCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LocalityCode = null);
public sealed record WorkDocument(string Kind, PoleIdentity? OldPole, PoleIdentity? NewPole,
    string Notes, WorkPhotoDescriptor[] Photos,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SchemaVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Progress = null);
public sealed record WorkSaveRequest(Guid ChangeId, Guid? ExpectedVersion, string Reason, WorkDocument Document);
public sealed record WorkDetails(Guid Id, Guid OwnerId, string Owner, int Revision,
    Guid Version, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, WorkDocument Document, bool Replayed = false);
public sealed record WorkReportData(string Progress, string Status, string OldSerialState, string NewSerialState,
    double? X, double? Y, double? OldX, double? OldY, double? NewX, double? NewY, string[] PhotoNames);
public sealed record WorkSummary(Guid Id, string Owner, string Kind, string? SerialNumber,
    int Revision, DateTime UpdatedAtUtc, int PhotoCount,
    string? OldSerialNumber = null, string? NewSerialNumber = null, string[]? Locations = null,
    WorkReportData? Report = null);
public sealed record WorkHistory(int Revision, string Actor, DateTime AtUtc, string Reason,
    string[] Changes, WorkDocument Document);
public sealed record WorkSettingsResponse(bool AllowQrGeneration, Guid Version);
public sealed record WorkSettingsRequest(bool AllowQrGeneration, Guid ExpectedVersion);

public sealed class WorkException(string code, int status = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}