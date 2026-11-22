using System.Security.Claims;
using GeoCapture.Api.Auth;
using GeoCapture.Api.Data;
using GeoCapture.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GeoCapture.Api.Uploads;

public static class UploadEndpoints
{
    public static void MapGeoCaptureUploads(this WebApplication app)
    {
        var uploads = app.MapGroup("/api/uploads").RequireAuthorization();
        uploads.MapGet("/limits", (IOptions<UploadOptions> options) => Results.Ok(options.Value));
        uploads.MapPost("", Create);
        uploads.MapGet("", ListOwn);
        uploads.MapGet("/{id:guid}", Details);
        uploads.MapGet("/{uploadId:guid}/photos/{photoId:guid}/{variant}", Image);

        app.MapGet("/api/admin/uploads", ListAll)
            .RequireAuthorization(AuthSetup.AdminPolicy);
    }

    private static async Task<IResult> Create(
        HttpContext context, AppDbContext database, UploadReader reader,
        TimeProvider clock, CancellationToken token)
    {
        if (!TryUserId(context, out var ownerId)) return Results.Unauthorized();

        PreparedUpload prepared;
        try
        {
            prepared = await reader.ReadAsync(context.Request, token);
        }
        catch (UploadException error)
        {
            return Results.Json(new ApiError(error.Code), statusCode: error.StatusCode);
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        {
            return Results.Json(new ApiError("upload_too_large"), statusCode: 413);
        }

        var existing = await FindExisting(database, ownerId, prepared.ClientUploadId, token);
        if (existing is not null) return Replay(existing, prepared.PayloadSha256);

        var upload = new PhotoUpload
        {
            ClientUploadId = prepared.ClientUploadId,
            OwnerId = ownerId,
            UploadedByUsername = context.User.Identity?.Name ?? "",
            SerialNumber = prepared.SerialNumber,
            PayloadSha256 = prepared.PayloadSha256,
            UploadedAtUtc = clock.GetUtcNow().UtcDateTime,
            Photos = prepared.Photos.ToList()
        };

        database.PhotoUploads.Add(upload);
        try
        {
            // One SaveChanges transaction persists the complete batch and both image variants.
            await database.SaveChangesAsync(token);
        }
        catch (DbUpdateException)
        {
            // A concurrent retry can win the unique (OwnerId, ClientUploadId) index.
            database.ChangeTracker.Clear();
            existing = await FindExisting(database, ownerId, prepared.ClientUploadId, token);
            if (existing is not null) return Replay(existing, prepared.PayloadSha256);
            throw;
        }

        return Results.Created($"/api/uploads/{upload.Id:D}", ToDetails(upload, false));
    }

    private static async Task<PhotoUpload?> FindExisting(
        AppDbContext database, Guid ownerId, Guid clientId, CancellationToken token) =>
        await database.PhotoUploads.AsNoTracking().Include(x => x.Photos)
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.ClientUploadId == clientId, token);

    private static IResult Replay(PhotoUpload upload, string payloadHash) =>
        upload.PayloadSha256 == payloadHash
            ? Results.Ok(ToDetails(upload, true))
            : Results.Conflict(new ApiError("upload_id_conflict"));

    private static Task<IResult> ListOwn(
        HttpContext context, AppDbContext database, int? page, int? pageSize,
        CancellationToken token) => List(context, database, page, pageSize, false, token);

    private static Task<IResult> ListAll(
        HttpContext context, AppDbContext database, int? page, int? pageSize,
        CancellationToken token) => List(context, database, page, pageSize, true, token);

    private static async Task<IResult> List(
        HttpContext context, AppDbContext database, int? page, int? pageSize,
        bool all, CancellationToken token)
    {
        if (!TryUserId(context, out var ownerId)) return Results.Unauthorized();
        var number = page ?? 1;
        var size = pageSize ?? 20;
        if (number < 1 || size is < 1 or > 50 || (long)(number - 1) * size > int.MaxValue)
        {
            return Results.BadRequest(new ApiError("invalid_page"));
        }

        var query = database.PhotoUploads.AsNoTracking();
        if (!all) query = query.Where(x => x.OwnerId == ownerId);
        var total = await query.CountAsync(token);
        var items = await query.OrderByDescending(x => x.UploadedAtUtc).ThenByDescending(x => x.Id)
            .Skip((number - 1) * size).Take(size)
            .Select(x => new UploadSummary(x.Id, x.ClientUploadId, x.SerialNumber,
                x.OwnerId, x.UploadedByUsername, x.UploadedAtUtc, x.Photos.Count, x.Version))
            .ToArrayAsync(token);

        return Results.Ok(new UploadPage(number, size, total, items));
    }

    private static async Task<IResult> Details(
        Guid id, HttpContext context, AppDbContext database, CancellationToken token)
    {
        if (!TryUserId(context, out var ownerId)) return Results.Unauthorized();
        var admin = context.User.IsInRole(AuthSetup.AdminRole);
        var upload = await database.PhotoUploads.AsNoTracking().Include(x => x.Photos)
            .SingleOrDefaultAsync(x => x.Id == id && (admin || x.OwnerId == ownerId), token);

        return upload is null ? Results.NotFound() : Results.Ok(ToDetails(upload, false));
    }

    private static async Task<IResult> Image(
        Guid uploadId, Guid photoId, string variant, HttpContext context,
        AppDbContext database, CancellationToken token)
    {
        if (!TryUserId(context, out var ownerId)) return Results.Unauthorized();
        if (variant is not ("original" or "stamped")) return Results.NotFound();
        var stamped = variant == "stamped";
        var admin = context.User.IsInRole(AuthSetup.AdminRole);

        var photo = await database.CapturedPhotos.AsNoTracking()
            .Where(x => x.Id == photoId && x.UploadId == uploadId &&
                (admin || x.Upload.OwnerId == ownerId))
            .Select(x => new
            {
                Bytes = stamped ? x.Content.StampedBytes : x.Content.OriginalBytes,
                MimeType = stamped ? "image/jpeg" : x.OriginalMimeType
            }).SingleOrDefaultAsync(token);

        if (photo is null) return Results.NotFound();
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var extension = photo.MimeType == "image/png" ? "png" : "jpg";
        return Results.File(photo.Bytes, photo.MimeType,
            $"{photoId:D}.{variant}.{extension}");
    }

    private static bool TryUserId(HttpContext context, out Guid id) =>
        Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out id);

    private static UploadDetails ToDetails(PhotoUpload upload, bool replayed) => new(
        upload.Id, upload.ClientUploadId, upload.SerialNumber, upload.OwnerId,
        upload.UploadedByUsername, upload.UploadedAtUtc, upload.Version, replayed,
        upload.Photos.OrderBy(x => x.Position).Select(photo => new UploadedPhotoResponse(
            photo.Id, photo.ClientPhotoId, photo.Position, photo.Latitude, photo.Longitude,
            photo.AccuracyMeters, photo.CoordinatesText, photo.CapturedAtUtc,
            photo.LocationMeasuredAtUtc, photo.OriginalMimeType, photo.OriginalWidth,
            photo.OriginalHeight, photo.StampedWidth, photo.StampedHeight,
            photo.OriginalByteLength, photo.StampedByteLength, photo.OriginalSha256,
            photo.StampedSha256,
            $"/api/uploads/{upload.Id:D}/photos/{photo.Id:D}/original",
            $"/api/uploads/{upload.Id:D}/photos/{photo.Id:D}/stamped")).ToArray());
}