using GeoCapture.Api.Auth;
using Microsoft.EntityFrameworkCore;

namespace GeoCapture.Api.FieldWork;

public static partial class WorkEndpoints
{
    private static async Task<IResult> ExportCsv(bool? all, HttpContext context,
        WorkDbContext db, TimeProvider clock, CancellationToken token)
    {
        var admin = context.User.IsInRole(AuthSetup.AdminRole);
        if (all == true && !admin) return Results.Forbid();
        WorkListFilters filters;
        try { filters = WorkListFilters.Read(context.Request.Query); }
        catch (WorkException error) { return Results.BadRequest(new ApiError(error.Code)); }
        if (filters.Owner.Length != 0 && !admin) return Results.Forbid();
        if (filters.Owner.Length != 0 && all != true)
            return Results.BadRequest(new ApiError("work_invalid_filters"));

        var query = WorkListSearch.Query(db, filters);
        if (all != true) query = query.Where(record => record.OwnerId == UserId(context));
        // One database query captures each record together with its immutable revision.
        var snapshots = await (from record in query
            join revision in db.Revisions.AsNoTracking()
                on new { record.Id, Number = record.Revision } equals new { Id = revision.WorkId, revision.Number }
            orderby record.UpdatedAtUtc descending, record.Id
            select new { Record = record, Revision = revision })
            .Take(WorkExports.MaxCsvWorks + 1).ToArrayAsync(token);
        if (snapshots.Length > WorkExports.MaxCsvWorks)
            return Results.BadRequest(new ApiError("work_export_too_many"));
        var works = snapshots.Select(snapshot => Response(snapshot.Record, snapshot.Revision));
        ExportHeaders(context);
        return Results.File(WorkExports.Csv(works), "text/csv; charset=utf-8",
            $"geocapture-works-{clock.GetUtcNow():yyyyMMdd-HHmmss}.csv");
    }

    private static async Task<IResult> ExportZip(Guid id, int? revision, HttpContext context,
        WorkDbContext db, TimeProvider clock, CancellationToken token)
    {
        if (revision is < 1) return Results.BadRequest(new ApiError("work_export_invalid_revision"));
        var record = await Visible(context, db).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (record is null) return Results.NotFound();
        var number = revision ?? record.Revision;
        var snapshot = await db.Revisions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkId == id && x.Number == number, token);
        if (snapshot is null) return Results.NotFound();
        var work = Response(record, snapshot);
        var ids = work.Document.Photos.Select(photo => photo.Id).ToArray();
        var photos = await db.Photos.AsNoTracking().Where(photo => photo.WorkId == id && ids.Contains(photo.Id))
            .Select(photo => new WorkExportPhotoInfo(photo.Id, photo.OriginalMimeType,
                photo.OriginalHash, photo.StampedHash, (long)photo.OriginalBytes.Length + photo.StampedBytes.Length))
            .ToArrayAsync(token);
        if (photos.Length != ids.Length) return Results.NotFound();
        if (photos.Sum(photo => photo.Size) > WorkRules.MaxBatch)
            return Results.BadRequest(new ApiError("work_export_too_large"));

        var archive = await WorkExports.CreateZipAsync(work, snapshot, photos, async (photoId, cancellation) =>
            await db.Photos.AsNoTracking().Where(photo => photo.WorkId == id && photo.Id == photoId)
                .Select(photo => new WorkExportPhotoContent(photo.OriginalBytes, photo.StampedBytes))
                .SingleAsync(cancellation), clock.GetUtcNow(), context.RequestAborted);
        ExportHeaders(context);
        return Results.File(archive, "application/zip", WorkExports.ZipFilename(id, number));
    }

    private static void ExportHeaders(HttpContext context)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
}