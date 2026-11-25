using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GeoCapture.Api.FieldWork;

public static class WorkRules
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static readonly string[] Kinds = ["replacement", "repair", "removal", "pit"];
    public const int MaxPhotos = 10;
    public const long MaxFile = 8L * 1024 * 1024;
    public const long MaxBatch = 50L * 1024 * 1024;
    public const long MaxRequest = 64L * 1024 * 1024;

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new WorkException("work_invalid_manifest");
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    public static string GeneratedQr(PoleIdentity pole) => Serialize(new
    {
        type = "geocapture.pole",
        version = 1,
        ntetCode = pole.NtetCode,
        serialNumber = pole.SerialNumber
    });

    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) &&
        value == value.Trim() && value.Length <= max && !value.Any(char.IsControl);

    private static void Identity(PoleIdentity? pole, bool requireNtet)
    {
        if (pole is null || pole.NtetCode is null || !Text(pole.SerialNumber, 1024) ||
            (requireNtet && !Text(pole.NtetCode, 128)) ||
            (pole.NtetCode.Length > 0 && !Text(pole.NtetCode, 128)) ||
            pole.Source is not ("manual" or "scanned" or "generated") ||
            pole.RawCode?.Length > 4096 || (pole.Source == "scanned" && string.IsNullOrWhiteSpace(pole.RawCode)))
            throw new WorkException("work_invalid_identity");
        if (pole.Source == "generated" && pole.RawCode != GeneratedQr(pole))
            throw new WorkException("work_invalid_qr");
    }

    public static void Validate(WorkDocument doc, DateTime utcNow)
    {
        if (!Kinds.Contains(doc.Kind) || doc.Notes is null || doc.Notes.Length > 4000 ||
            doc.Photos is null || doc.Photos.Length is < 1 or > MaxPhotos || doc.Photos.Any(x => x is null) ||
            doc.Photos.Select(x => x.Id).Distinct().Count() != doc.Photos.Length)
            throw new WorkException("work_invalid_manifest");

        var roles = doc.Kind switch
        {
            "replacement" => new[] { "old", "new" },
            "repair" => ["before", "after"],
            "removal" => ["pole"],
            _ => ["pit"]
        };
        if (roles.Any(role => !doc.Photos.Any(photo => photo.Role == role)) ||
            doc.Photos.Any(photo => !roles.Contains(photo.Role)))
            throw new WorkException("work_required_photo");

        if (doc.Kind != "pit") Identity(doc.OldPole, false);
        if (doc.OldPole is { Source: "generated" }) throw new WorkException("work_invalid_identity");
        if (doc.Kind == "replacement") Identity(doc.NewPole, true);
        else if (doc.NewPole is not null) throw new WorkException("work_invalid_identity");
        if (doc.Kind == "pit" && doc.OldPole is not null) throw new WorkException("work_invalid_identity");

        foreach (var photo in doc.Photos)
        {
            if (photo.Id == Guid.Empty || !double.IsFinite(photo.Latitude) || Math.Abs(photo.Latitude) > 90 ||
                !double.IsFinite(photo.Longitude) || Math.Abs(photo.Longitude) > 180 ||
                !double.IsFinite(photo.AccuracyMeters) || photo.AccuracyMeters < 0 ||
                photo.CapturedAtUtc.Kind != DateTimeKind.Utc || photo.MeasuredAtUtc.Kind != DateTimeKind.Utc ||
                photo.CapturedAtUtc < new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) ||
                photo.CapturedAtUtc > utcNow.AddMinutes(5) ||
                (photo.CapturedAtUtc - photo.MeasuredAtUtc).Duration() > TimeSpan.FromSeconds(30) ||
                photo.UtcOffsetMinutes is < -840 or > 840 || !Text(photo.Locality, 160) || !Text(photo.County, 160) ||
                photo.PlaceSource is not ("manual" or "geocoder") ||
                photo.OriginalMimeType is not ("image/jpeg" or "image/png") ||
                photo.OverlayText != Overlay(photo))
                throw new WorkException("work_invalid_photo_metadata");
            if (photo.Role == "old" && !photo.SerialVisible)
                throw new WorkException("work_serial_not_confirmed");
        }
    }

    private static string Coordinate(double value)
    {
        var result = value.ToString("F6", CultureInfo.InvariantCulture);
        return result == "-0.000000" ? "0.000000" : result;
    }

    public static string Overlay(WorkPhotoDescriptor photo)
    {
        var offset = photo.UtcOffsetMinutes;
        var local = photo.CapturedAtUtc.AddMinutes(offset).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var zone = $"UTC{(offset >= 0 ? "+" : "-")}{Math.Abs(offset) / 60:00}:{Math.Abs(offset) % 60:00}";
        return $"{Coordinate(photo.Latitude)}, {Coordinate(photo.Longitude)}\n" +
            $"{photo.Locality}, {photo.County}\n{local} ({zone})";
    }

    public static string[] Changes(WorkDocument? old, WorkDocument next)
    {
        if (old is null) return ["created"];
        var result = new List<string>();
        if (old.Kind != next.Kind) result.Add("kind");
        if (old.Notes != next.Notes) result.Add("notes");
        if (old.OldPole != next.OldPole) result.Add("oldPole");
        if (old.NewPole != next.NewPole) result.Add("newPole");
        if (Serialize(old.Photos) != Serialize(next.Photos)) result.Add("photos");
        return result.ToArray();
    }
}