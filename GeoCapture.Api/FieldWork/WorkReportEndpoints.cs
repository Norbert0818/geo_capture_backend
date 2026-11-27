using GeoCapture.Api.Auth;
using Microsoft.EntityFrameworkCore;

namespace GeoCapture.Api.FieldWork;

public static partial class WorkEndpoints
{
    private static WorkListFilters ReadPlaceFilters(HttpContext context)
    {
        var filters = WorkListFilters.Read(context.Request.Query);
        if (filters.CountyCode.Length == 0 && filters.LocalityCode.Length == 0) return filters;
        var catalogue = context.RequestServices.GetRequiredService<RomaniaCatalog>();
        RomaniaLocality? locality = null;
        if (filters.LocalityCode.Length != 0 && !catalogue.Localities.TryGetValue(filters.LocalityCode, out locality))
            throw new WorkException("work_invalid_filters");
        var code = filters.CountyCode.Length != 0 ? filters.CountyCode : locality!.CountyCode;
        if (!catalogue.Counties.TryGetValue(code, out var county) || locality is not null && locality.CountyCode != code)
            throw new WorkException("work_invalid_filters");
        if (filters.County.Length != 0 && RomaniaCatalog.Fold(filters.County) != RomaniaCatalog.Fold(county.Name) ||
            locality is not null && filters.Locality.Length != 0 && RomaniaCatalog.Fold(filters.Locality) != RomaniaCatalog.Fold(locality.Name))
            throw new WorkException("work_invalid_filters");
        return filters with { CountyCode = code, County = county.Name, Locality = locality?.Name ?? filters.Locality };
    }

    private static async Task<IResult> ExportReportCsv(bool? all, HttpContext context,
        WorkDbContext db, TimeProvider clock, CancellationToken token)
    {
        try
        {
            var filters = ReadPlaceFilters(context);
            if (filters.Kind.Length == 0) throw new WorkException("work_report_type_required");
            var languages = context.Request.Query["lang"];
            if (languages.Count > 1 || languages.Count == 1 && languages[0] is not ("en" or "ro"))
                throw new WorkException("work_invalid_filters");
            var query = WorkListSearch.Query(db, filters);
            if (all != true) query = query.Where(x => x.OwnerId == UserId(context));
            if (filters.Owner.Length != 0 && all != true) throw new WorkException("work_invalid_filters");
            var records = await query.OrderByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id)
                .Take(WorkExports.MaxCsvWorks + 1).ToArrayAsync(token);
            if (records.Length > WorkExports.MaxCsvWorks) throw new WorkException("work_export_too_many");
            var ids = records.Select(x => x.Id).ToArray();
            var versions = records.Select(x => x.Version).ToArray();
            var revisions = await db.Revisions.AsNoTracking().Where(x => ids.Contains(x.WorkId) && versions.Contains(x.ResultVersion)).ToArrayAsync(token);
            var byVersion = revisions.ToDictionary(x => (x.WorkId, x.Number));
            var details = records.Select(record => Response(record, byVersion[(record.Id, record.Revision)])).ToArray();
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var filename = $"{filters.Kind}_report_{clock.GetUtcNow():yyyyMMdd_HHmmss}.csv";
            return Results.File(WorkReports.Csv(details, filters.Kind, languages.FirstOrDefault() == "ro"),
                "text/csv; charset=utf-8", filename);
        }
        catch (WorkException error) { return Results.Json(new ApiError(error.Code), statusCode: error.Status); }
    }
}