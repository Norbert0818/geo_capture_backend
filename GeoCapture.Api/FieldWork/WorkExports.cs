using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace GeoCapture.Api.FieldWork;

public sealed record WorkExportPhotoInfo(Guid Id, string OriginalMimeType,
    string OriginalHash, string StampedHash, long Size);
public sealed record WorkExportPhotoContent(byte[] Original, byte[] Stamped);

public static class WorkExports
{
    public const int MaxCsvWorks = 1000;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] Columns = [
        "work_id", "revision", "work_type", "username", "old_serial_number", "old_ntet_code",
        "new_serial_number", "new_ntet_code", "photo_id", "photo_role", "latitude", "longitude",
        "locality", "county", "captured_at_utc", "captured_at_local", "utc_offset_minutes",
        "accuracy_meters", "location_measured_at_utc", "work_created_at_utc", "revision_saved_at_utc",
        "original_file", "stamped_file"
    ];

    public static string ZipFilename(Guid id, int revision) => $"work_{id:N}_v{revision}.zip";

    public static string PhotoPath(WorkPhotoDescriptor photo, int index, bool original)
    {
        var role = photo.Role is "old" or "new" or "before" or "after" or "pole" or "pit" ? photo.Role : "photo";
        var extension = original && photo.OriginalMimeType == "image/png" ? "png" : "jpg";
        return $"photos/{index + 1:D2}_{role}_{photo.Id:N}.{(original ? "original" : "stamped")}.{extension}";
    }

    private static string Coordinate(double value)
    {
        var result = value.ToString("F6", Invariant);
        return result == "-0.000000" ? "0.000000" : result;
    }

    // CSV is quoted consistently. Formula-looking text is neutralized for spreadsheet viewing.
    // Exact, unmodified identifiers are also present in the ZIP's manifest.json.
    public static string CsvCell(string value)
    {
        var significant = value.TrimStart();
        var normalized = significant.Normalize(NormalizationForm.FormKC);
        var numericNegative = significant.StartsWith('-') &&
            double.TryParse(significant, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                Invariant, out var number) && double.IsFinite(number);
        if (normalized.Length > 0 && "=+-@".Contains(normalized[0]) && !numericNegative)
            value = "\t" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static byte[] Csv(IEnumerable<WorkDetails> works)
    {
        var text = new StringBuilder();
        void Row(IEnumerable<string> values) => text.AppendJoin(',', values.Select(CsvCell)).Append("\r\n");
        Row(Columns);
        foreach (var work in works)
        {
            var document = work.Document;
            for (var index = 0; index < document.Photos.Length; index++)
            {
                var photo = document.Photos[index];
                var utc = DateTime.SpecifyKind(photo.CapturedAtUtc, DateTimeKind.Utc);
                var local = new DateTimeOffset(utc).ToOffset(TimeSpan.FromMinutes(photo.UtcOffsetMinutes));
                Row([
                    work.Id.ToString("D"), work.Revision.ToString(Invariant), document.Kind, work.Owner,
                    document.OldPole?.SerialNumber ?? "", document.OldPole?.NtetCode ?? "",
                    document.NewPole?.SerialNumber ?? "", document.NewPole?.NtetCode ?? "",
                    photo.Id.ToString("D"), photo.Role, Coordinate(photo.Latitude), Coordinate(photo.Longitude),
                    photo.Locality, photo.County, utc.ToString("O", Invariant),
                    local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", Invariant),
                    photo.UtcOffsetMinutes.ToString(Invariant), photo.AccuracyMeters.ToString("G", Invariant),
                    photo.MeasuredAtUtc.ToString("O", Invariant), work.CreatedAtUtc.ToString("O", Invariant),
                    work.UpdatedAtUtc.ToString("O", Invariant), PhotoPath(photo, index, true), PhotoPath(photo, index, false)
                ]);
            }
        }
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text.ToString())];
    }

    // Finish the archive on a seekable temporary file before returning any response bytes.
    // Results.File owns this stream; DeleteOnClose removes it on completion or disconnect.
    public static async Task<FileStream> CreateZipAsync(WorkDetails work,
        FieldWorkRevision revision, WorkExportPhotoInfo[] photos,
        Func<Guid, CancellationToken, Task<WorkExportPhotoContent>> loadPhoto,
        DateTimeOffset exportedAt, CancellationToken token, string? temporaryDirectory = null)
    {
        token.ThrowIfCancellationRequested();
        var path = Path.Combine(temporaryDirectory ?? Path.GetTempPath(), $"geocapture-export-{Guid.NewGuid():N}.zip");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        try
        {
            await WriteZipAsync(stream, work, revision, photos, loadPhoto, exportedAt, token);
            token.ThrowIfCancellationRequested();
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static async Task WriteZipAsync(Stream destination, WorkDetails work,
        FieldWorkRevision revision, WorkExportPhotoInfo[] photos,
        Func<Guid, CancellationToken, Task<WorkExportPhotoContent>> loadPhoto,
        DateTimeOffset exportedAt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var byId = photos.ToDictionary(photo => photo.Id);
        var files = work.Document.Photos.Select((photo, index) => new {
            photo.Id, photo.Role, Original = PhotoPath(photo, index, true),
            Stamped = PhotoPath(photo, index, false), byId[photo.Id].OriginalHash, byId[photo.Id].StampedHash
        }).ToArray();
        var manifest = new {
            Format = "geocapture.work-export", FormatVersion = 1, ExportedAtUtc = exportedAt.UtcDateTime,
            Work = work, Revision = new { revision.ActorName, revision.AtUtc, revision.Reason,
                Changes = WorkRules.Read<string[]>(revision.ChangesJson) }, Files = files
        };

        await using var archive = await ZipArchive.CreateAsync(destination, ZipArchiveMode.Create,
            leaveOpen: true, entryNameEncoding: null, cancellationToken: token);
        async Task Entry(string name, ReadOnlyMemory<byte> content)
        {
            token.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            await using var stream = await entry.OpenAsync(token);
            await stream.WriteAsync(content, token);
        }
        await Entry("manifest.json", Encoding.UTF8.GetBytes(WorkRules.Serialize(manifest)));
        await Entry("photos.csv", Csv([work]));
        long written = 0;
        for (var index = 0; index < work.Document.Photos.Length; index++)
        {
            var photo = work.Document.Photos[index];
            var content = await loadPhoto(photo.Id, token);
            var info = byId[photo.Id];
            written += (long)content.Original.Length + content.Stamped.Length;
            if (written > WorkRules.MaxBatch ||
                !string.Equals(WorkRules.Hash(content.Original), info.OriginalHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(WorkRules.Hash(content.Stamped), info.StampedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Export photo content did not match the stored snapshot.");
            await Entry(PhotoPath(photo, index, true), content.Original);
            await Entry(PhotoPath(photo, index, false), content.Stamped);
        }
    }
}