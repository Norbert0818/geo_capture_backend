using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace GeoCapture.Api.FieldWork;

public sealed record WorkListFilters(string Kind = "", string Serial = "", string Ntet = "",
    string Locality = "", string County = "", string Owner = "", string FromDate = "", string ToDate = "")
{
    public bool IsEmpty => this == new WorkListFilters();

    public static WorkListFilters Read(IQueryCollection query)
    {
        string Text(string key, int maximum)
        {
            if (!query.TryGetValue(key, out var values)) return "";
            if (values.Count != 1) throw new WorkException("work_invalid_filters");
            var raw = values[0] ?? "";
            var value = raw.Trim();
            if (value.Length > maximum || raw.Any(char.IsControl))
                throw new WorkException("work_invalid_filters");
            return value;
        }

        string Date(string key)
        {
            var value = Text(key, 10);
            if (value.Length != 0 && !DateOnly.TryParseExact(value, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new WorkException("work_invalid_filters");
            return value;
        }

        var result = new WorkListFilters(Text("kind", 20), Text("serial", 1024), Text("ntet", 128),
            Text("locality", 160), Text("county", 160), Text("owner", 256), Date("fromDate"), Date("toDate"));
        if (result.Kind is not ("" or "replacement" or "repair" or "removal" or "pit") ||
            result.FromDate.Length != 0 && result.ToDate.Length != 0 &&
            string.CompareOrdinal(result.FromDate, result.ToDate) > 0)
            throw new WorkException("work_invalid_filters");
        return result;
    }
}

public sealed record WorkSearchSql(string Text, string[] Values);

public static class WorkListSearch
{
    public static IQueryable<FieldWorkRecord> Query(WorkDbContext db, WorkListFilters filters)
    {
        if (filters.IsEmpty) return db.Records.AsNoTracking();
        var postgres = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";
        if (!postgres && db.Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
            throw new NotSupportedException("Field work search requires PostgreSQL or the SQLite test provider.");
        var statement = Build(filters, postgres);
        using var command = db.Database.GetDbConnection().CreateCommand();
        var parameters = statement.Values.Select((value, index) =>
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"f{index}";
            parameter.Value = value;
            return (object)parameter;
        }).ToArray();
        return db.Records.FromSqlRaw(statement.Text, parameters).AsNoTracking();
    }

    // Only fixed SQL fragments are composed. Every user value is a separate parameter.
    public static WorkSearchSql Build(WorkListFilters filters, bool postgres)
    {
        var values = new List<string>();
        var conditions = new List<string>();
        string Parameter(string value) { var name = $"@f{values.Count}"; values.Add(value); return name; }
        string Field(string path, bool photo = false)
        {
            if (!postgres) return $"json_extract({(photo ? "p.value" : "r.\"DocumentJson\"")}, '$.{path}')";
            var parts = path.Split('.');
            var root = photo ? "p.value" : "r.\"DocumentJson\"::jsonb";
            return $"({root}" + string.Concat(parts.Select((part, index) =>
                $"{(index == parts.Length - 1 ? "->>" : "->")}'{part}'")) + ")";
        }
        string Contains(string expression, string parameter) => postgres
            ? $"strpos(lower(COALESCE({expression}, '')), lower({parameter})) > 0"
            : $"instr(lower(COALESCE({expression}, '')), lower({parameter})) > 0";
        void Identity(string value, string property)
        {
            if (value.Length == 0) return;
            var parameter = Parameter(value);
            conditions.Add($"({Contains(Field($"oldPole.{property}"), parameter)} OR {Contains(Field($"newPole.{property}"), parameter)})");
        }
        if (filters.Kind.Length != 0) conditions.Add($"{Field("kind")} = {Parameter(filters.Kind)}");
        Identity(filters.Serial, "serialNumber");
        Identity(filters.Ntet, "ntetCode");
        if (filters.Owner.Length != 0) conditions.Add(Contains("w.\"OwnerName\"", Parameter(filters.Owner)));

        var photoConditions = new List<string>();
        if (filters.Locality.Length != 0) photoConditions.Add(Contains(Field("locality", true), Parameter(filters.Locality)));
        if (filters.County.Length != 0) photoConditions.Add(Contains(Field("county", true), Parameter(filters.County)));
        if (filters.FromDate.Length != 0 || filters.ToDate.Length != 0)
        {
            var captured = Field("capturedAtUtc", true);
            var offset = Field("utcOffsetMinutes", true);
            var date = postgres
                ? $"to_char(({captured}::timestamptz AT TIME ZONE 'UTC') + ({offset}::integer * INTERVAL '1 minute'), 'YYYY-MM-DD')"
                : $"strftime('%Y-%m-%d', {captured}, printf('%+d minutes', COALESCE({offset}, 0)))";
            if (filters.FromDate.Length != 0) photoConditions.Add($"{date} >= {Parameter(filters.FromDate)}");
            if (filters.ToDate.Length != 0) photoConditions.Add($"{date} <= {Parameter(filters.ToDate)}");
        }
        if (photoConditions.Count != 0)
        {
            var photos = postgres ? "jsonb_array_elements(r.\"DocumentJson\"::jsonb->'photos') AS p(value)"
                : "json_each(r.\"DocumentJson\", '$.photos') AS p";
            conditions.Add($"EXISTS (SELECT 1 FROM {photos} WHERE {string.Join(" AND ", photoConditions)})");
        }
        var schema = postgres ? "\"geo_capture\"." : "";
        var sql = $"SELECT w.* FROM {schema}\"FieldWorks\" AS w " +
            $"INNER JOIN {schema}\"FieldWorkRevisions\" AS r ON r.\"WorkId\" = w.\"Id\" AND r.\"Number\" = w.\"Revision\"";
        if (conditions.Count != 0) sql += " WHERE " + string.Join(" AND ", conditions);
        return new(sql, values.ToArray());
    }

    public static WorkSummary Summary(FieldWorkRecord record, WorkDocument document) =>
        new(record.Id, record.OwnerName, document.Kind,
            document.NewPole?.SerialNumber ?? document.OldPole?.SerialNumber,
            record.Revision, record.UpdatedAtUtc, document.Photos.Length,
            document.OldPole?.SerialNumber, document.NewPole?.SerialNumber,
            document.Photos.Select(photo => $"{photo.Locality}, {photo.County}").Distinct().ToArray());
}