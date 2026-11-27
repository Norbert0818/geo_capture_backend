using System.Text.Json;
using GeoCapture.Api.Uploads;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace GeoCapture.Api.FieldWork;

public sealed record PreparedWork(WorkSaveRequest Request, List<FieldWorkPhoto> Photos, string Hash);

public sealed class WorkReader(WorkDbContext database, TimeProvider clock, RomaniaCatalog? catalogue = null)
{
    public async Task<PreparedWork> ReadAsync(Guid workId, HttpRequest request, CancellationToken token)
    {
        if (!request.HasFormContentType) throw new WorkException("work_multipart_required", 415);
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(new FormOptions
            {
                MultipartBodyLengthLimit = WorkRules.MaxRequest,
                ValueLengthLimit = 128 * 1024,
                ValueCountLimit = 1 + WorkRules.MaxPhotos * 2
            }, token);
        }
        catch (InvalidDataException) { throw new WorkException("work_upload_too_large", 413); }

        if (form.Count != 1 || !form.TryGetValue("manifest", out var manifest) || manifest.Count != 1 ||
            manifest[0] is null || manifest[0]!.Length > 100_000 || form.Files.Count > WorkRules.MaxPhotos * 2)
            throw new WorkException("work_invalid_manifest");
        WorkSaveRequest input;
        try { input = WorkRules.Read<WorkSaveRequest>(manifest[0]!); }
        catch (JsonException) { throw new WorkException("work_invalid_manifest"); }
        if (workId == Guid.Empty || input.ChangeId == Guid.Empty || input.Document is null ||
            string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length is < 3 or > 500)
            throw new WorkException("work_invalid_manifest");
        input = input with { Reason = input.Reason.Trim() };
        WorkRules.Validate(input.Document, clock.GetUtcNow().UtcDateTime);
        foreach (var photo in input.Document.Photos)
        {
            if ((photo.CountyCode is not null || photo.LocalityCode is not null) && catalogue is null)
                throw new WorkException("work_catalogue_unavailable", 503);
            catalogue?.ValidatePhoto(photo);
        }
        if (form.Files.Select(x => x.Name).Distinct().Count() != form.Files.Count)
            throw new WorkException("work_invalid_photo_pair");

        var files = form.Files.ToDictionary(x => x.Name);
        var photos = new List<FieldWorkPhoto>();
        long total = 0;
        foreach (var descriptor in input.Document.Photos)
        {
            var originalName = $"original_{descriptor.Id:D}";
            var stampedName = $"stamped_{descriptor.Id:D}";
            var hasOriginal = files.Remove(originalName, out var originalFile);
            var hasStamped = files.Remove(stampedName, out var stampedFile);
            if (hasOriginal != hasStamped) throw new WorkException("work_invalid_photo_pair");
            FieldWorkPhoto photo;
            if (!hasOriginal)
            {
                photo = await database.Photos.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == descriptor.Id && x.WorkId == workId, token)
                    ?? throw new WorkException("work_photo_missing");
                if (photo.DescriptorJson != WorkRules.Serialize(descriptor))
                    throw new WorkException("work_photo_immutable");
            }
            else
            {
                var original = await Bytes(originalFile!, token);
                var stamped = await Bytes(stampedFile!, token);
                try
                {
                    var first = PhotoFileInspector.Inspect(original, 50_000_000);
                    var second = PhotoFileInspector.Inspect(stamped, 50_000_000);
                    if (first.MimeType != descriptor.OriginalMimeType || second.MimeType != "image/jpeg")
                        throw new WorkException("work_invalid_photo_pair");
                }
                catch (UploadException) { throw new WorkException("work_invalid_photo_pair"); }
                photo = new FieldWorkPhoto
                {
                    Id = descriptor.Id, WorkId = workId, DescriptorJson = WorkRules.Serialize(descriptor),
                    OriginalMimeType = descriptor.OriginalMimeType, OriginalBytes = original, StampedBytes = stamped,
                    OriginalHash = WorkRules.Hash(original), StampedHash = WorkRules.Hash(stamped)
                };
            }
            total += photo.OriginalBytes.LongLength + photo.StampedBytes.LongLength;
            if (total > WorkRules.MaxBatch) throw new WorkException("work_upload_too_large", 413);
            photos.Add(photo);
        }
        if (files.Count != 0) throw new WorkException("work_invalid_photo_pair");
        var hash = WorkRules.Hash(WorkRules.Serialize(new
        {
            input.Document, input.Reason,
            photos = photos.Select(x => new { x.Id, x.OriginalHash, x.StampedHash })
        }));
        return new(input, photos, hash);
    }

    private static async Task<byte[]> Bytes(IFormFile file, CancellationToken token)
    {
        if (file.Length <= 0 || file.Length > WorkRules.MaxFile)
            throw new WorkException("work_upload_too_large", 413);
        await using var input = file.OpenReadStream();
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > WorkRules.MaxFile) throw new WorkException("work_upload_too_large", 413);
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}