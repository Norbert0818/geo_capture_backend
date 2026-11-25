using System.Security.Claims;
using GeoCapture.Api.Auth;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace GeoCapture.Api.FieldWork;

public static class WorkEndpoints
{
    public static void MapFieldWork(this WebApplication app)
    {
        var group = app.MapGroup("/api/field-work").RequireAuthorization();
        group.MapGet("/settings", async (WorkDbContext db, CancellationToken token) =>
        {
            var settings = await db.Settings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, token);
            return Results.Ok(new WorkSettingsResponse(settings?.AllowQrGeneration ?? false, settings?.Version ?? Guid.Empty));
        });
        group.MapPost("/settings", Settings).RequireAuthorization(AuthSetup.AdminPolicy);
        group.MapGet("", List);
        group.MapGet("/{id:guid}", Details);
        group.MapGet("/{id:guid}/history", History);
        group.MapGet("/{id:guid}/photos/{photoId:guid}/{variant}", Image);
        group.MapPost("/{id:guid}/revisions", Save);
    }

    public static void UseFieldWorkLimits(this WebApplication app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/field-work") && HttpMethods.IsPost(context.Request.Method))
        {
            if (context.Request.ContentLength > WorkRules.MaxRequest)
            {
                context.Response.StatusCode = 413;
                await context.Response.WriteAsJsonAsync(new ApiError("work_upload_too_large"));
                return;
            }
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = WorkRules.MaxRequest;
        }
        await next(context);
    });

    private static Guid UserId(HttpContext context) => Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static IQueryable<FieldWorkRecord> Visible(HttpContext context, WorkDbContext db) =>
        db.Records.Where(x => context.User.IsInRole(AuthSetup.AdminRole) || x.OwnerId == UserId(context));

    private static async Task<IResult> Settings(WorkSettingsRequest input, HttpContext context, WorkDbContext db, TimeProvider clock, CancellationToken token)
    {
        var current = await db.Settings.SingleOrDefaultAsync(x => x.Id == 1, token);
        if ((current?.Version ?? Guid.Empty) != input.ExpectedVersion)
            return Results.Conflict(new ApiError("work_conflict"));
        if (current is null) { current = new FieldWorkSettings(); db.Settings.Add(current); }
        current.AllowQrGeneration = input.AllowQrGeneration;
        current.Version = Guid.NewGuid(); current.ChangedBy = UserId(context); current.ChangedAtUtc = clock.GetUtcNow().UtcDateTime;
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException) { return Results.Conflict(new ApiError("work_conflict")); }
        return Results.Ok(new WorkSettingsResponse(current.AllowQrGeneration, current.Version));
    }

    private static async Task<IResult> List(bool? all, int? page, HttpContext context, WorkDbContext db, CancellationToken token)
    {
        var admin = context.User.IsInRole(AuthSetup.AdminRole);
        if (all == true && !admin) return Results.Forbid();
        WorkListFilters filters;
        try { filters = WorkListFilters.Read(context.Request.Query); }
        catch (WorkException error) { return Results.BadRequest(new ApiError(error.Code)); }
        if (filters.Owner.Length != 0 && !admin) return Results.Forbid();
        if (filters.Owner.Length != 0 && all != true) return Results.BadRequest(new ApiError("work_invalid_filters"));
        var number = page ?? 1;
        if (number < 1 || number > 100000) return Results.BadRequest(new ApiError("work_invalid_page"));
        var query = WorkListSearch.Query(db, filters);
        if (all != true) query = query.Where(x => x.OwnerId == UserId(context));
        var total = await query.CountAsync(token);
        var records = await query.OrderByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id)
            .Skip((number - 1) * 20).Take(20).ToArrayAsync(token);
        var ids = records.Select(x => x.Id).ToArray();
        // Use the immutable revision numbers captured with this page, even if an edit finishes now.
        var numbers = records.Select(x => x.Revision).Distinct().ToArray();
        var snapshots = await db.Revisions.AsNoTracking().Where(x => ids.Contains(x.WorkId) &&
            numbers.Contains(x.Number)).ToArrayAsync(token);
        var revisions = snapshots.ToDictionary(x => (x.WorkId, x.Number));
        var items = records.Select(record => WorkListSearch.Summary(record,
            WorkRules.Read<WorkDocument>(revisions[(record.Id, record.Revision)].DocumentJson)));
        return Results.Ok(new { page = number, total, items });
    }

    private static async Task<IResult> Details(Guid id, HttpContext context, WorkDbContext db, CancellationToken token)
    {
        var record = await Visible(context, db).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (record is null) return Results.NotFound();
        var revision = await db.Revisions.AsNoTracking().SingleAsync(x => x.WorkId == id && x.Number == record.Revision, token);
        return Results.Ok(Response(record, revision));
    }

    private static WorkDetails Response(FieldWorkRecord record, FieldWorkRevision revision, bool replay = false) =>
        new(record.Id, record.OwnerId, record.OwnerName, revision.Number, revision.ResultVersion,
            record.CreatedAtUtc, revision.AtUtc, WorkRules.Read<WorkDocument>(revision.DocumentJson), replay);

    private static async Task<IResult> History(Guid id, HttpContext context, WorkDbContext db, CancellationToken token)
    {
        if (!await Visible(context, db).AnyAsync(x => x.Id == id, token)) return Results.NotFound();
        var revisions = await db.Revisions.AsNoTracking().Where(x => x.WorkId == id).OrderByDescending(x => x.Number).ToArrayAsync(token);
        return Results.Ok(new { items = revisions.Select(x => new WorkHistory(x.Number, x.ActorName, x.AtUtc,
            x.Reason, WorkRules.Read<string[]>(x.ChangesJson), WorkRules.Read<WorkDocument>(x.DocumentJson))) });
    }

    private static async Task<IResult> Image(Guid id, Guid photoId, string variant, HttpContext context, WorkDbContext db, CancellationToken token)
    {
        if (variant is not ("original" or "stamped") || !await Visible(context, db).AnyAsync(x => x.Id == id, token)) return Results.NotFound();
        var photo = await db.Photos.AsNoTracking().Where(x => x.WorkId == id && x.Id == photoId)
            .Select(x => new { Bytes = variant == "original" ? x.OriginalBytes : x.StampedBytes,
                Mime = variant == "original" ? x.OriginalMimeType : "image/jpeg" }).SingleOrDefaultAsync(token);
        if (photo is null) return Results.NotFound();
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(photo.Bytes, photo.Mime);
    }

    private static async Task<IResult> Save(Guid id, HttpContext context, WorkDbContext db, WorkReader reader, TimeProvider clock, CancellationToken token)
    {
        var record = await db.Records.SingleOrDefaultAsync(x => x.Id == id, token);
        if (record is not null && record.OwnerId != UserId(context) && !context.User.IsInRole(AuthSetup.AdminRole)) return Results.NotFound();
        PreparedWork prepared;
        try { prepared = await reader.ReadAsync(id, context.Request, token); }
        catch (WorkException error) { return Results.Json(new ApiError(error.Code), statusCode: error.Status); }
        catch (BadHttpRequestException) { return Results.Json(new ApiError("work_upload_too_large"), statusCode: 413); }
        var input = prepared.Request;
        var repeated = await db.Revisions.AsNoTracking().SingleOrDefaultAsync(x => x.WorkId == id && x.ChangeId == input.ChangeId, token);
        if (repeated is not null)
            return repeated.PayloadHash == prepared.Hash ? Results.Ok(Response(record!, repeated, true))
                : Results.Conflict(new ApiError("work_request_conflict"));
        if ((record?.Version) != input.ExpectedVersion) return Results.Conflict(new ApiError("work_conflict"));
        WorkDocument? prior = null;
        if (record is not null)
            prior = WorkRules.Read<WorkDocument>((await db.Revisions.AsNoTracking()
                .SingleAsync(x => x.WorkId == id && x.Number == record.Revision, token)).DocumentJson);
        var allowGeneration = await db.Settings.AsNoTracking().Where(x => x.Id == 1).Select(x => x.AllowQrGeneration).SingleOrDefaultAsync(token);
        if (input.Document.NewPole is { Source: "generated" } next && !allowGeneration && next != prior?.NewPole)
            return Results.BadRequest(new ApiError("work_qr_generation_disabled"));
        var now = clock.GetUtcNow().UtcDateTime;
        if (record is null)
        {
            record = new FieldWorkRecord { Id = id, OwnerId = UserId(context), OwnerName = context.User.Identity?.Name ?? "", CreatedAtUtc = now };
            db.Records.Add(record);
        }
        record.Revision++; record.Version = Guid.NewGuid(); record.UpdatedAtUtc = now;
        var revision = new FieldWorkRevision
        {
            Id = Guid.NewGuid(), WorkId = id, Number = record.Revision, ChangeId = input.ChangeId,
            ResultVersion = record.Version, ActorId = UserId(context), ActorName = context.User.Identity?.Name ?? "",
            AtUtc = now, Reason = input.Reason, PayloadHash = prepared.Hash,
            DocumentJson = WorkRules.Serialize(input.Document), ChangesJson = WorkRules.Serialize(WorkRules.Changes(prior, input.Document))
        };
        db.Revisions.Add(revision);
        foreach (var photo in prepared.Photos)
        {
            var existing = await db.Photos.AsNoTracking().SingleOrDefaultAsync(x => x.Id == photo.Id, token);
            if (existing is null) db.Photos.Add(photo);
            else if (existing.WorkId != id || existing.DescriptorJson != photo.DescriptorJson ||
                existing.OriginalHash != photo.OriginalHash || existing.StampedHash != photo.StampedHash)
                return Results.Conflict(new ApiError("work_photo_immutable"));
        }
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var retry = await db.Revisions.AsNoTracking().SingleOrDefaultAsync(x => x.WorkId == id && x.ChangeId == input.ChangeId, token);
            if (retry is not null && retry.PayloadHash == prepared.Hash)
            {
                var winner = await db.Records.AsNoTracking().SingleAsync(x => x.Id == id, token);
                return Results.Ok(Response(winner, retry, true));
            }
            return Results.Conflict(new ApiError("work_conflict"));
        }
        return Results.Ok(Response(record, revision));
    }
}