using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GeoCapture.Api.FieldWork;

public static class WorkReports
{
    public static string Status(WorkDocument doc) => ModernWorkRules.Progress(doc) == "in_progress"
        ? "in_progress" : doc.Kind switch { "repair" => "repaired", "replacement" => "replaced", "removal" => "removed", _ => "pit" };

    private static WorkPhotoDescriptor? Anchor(WorkDocument doc, string role)
    {
        var all = doc.Photos.OrderBy(x => x.CapturedAtUtc).ThenBy(x => x.Id).ToArray();
        return all.FirstOrDefault(x => x.Role == role) ?? all.FirstOrDefault(x => role switch
        {
            "old" => x.Role == "old_label", "new" => x.Role == "new_label",
            "after" => x.Role is "before" or "label", "pole" => x.Role == "label", _ => x.Role == role
        });
    }
    public static WorkReportData Data(Guid id, WorkDocument doc)
    {
        var old = Anchor(doc, "old"); var next = Anchor(doc, "new");
        var single = Anchor(doc, doc.Kind switch { "repair" => "after", "removal" => "pole", "pit" => "pit", _ => "old" });
        return new(ModernWorkRules.Progress(doc), Status(doc), ModernWorkRules.SerialState(doc.OldPole),
            ModernWorkRules.SerialState(doc.NewPole), single?.Latitude, single?.Longitude,
            old?.Latitude, old?.Longitude, next?.Latitude, next?.Longitude,
            doc.Photos.Select((photo, index) => PhotoPath(id, doc, photo, index, false)["photos/".Length..]).ToArray());
    }
    public static string PhotoPath(Guid workId, WorkDocument doc, WorkPhotoDescriptor photo, int index, bool original)
    {
        var pole = photo.Role is "new" or "new_label" ? doc.NewPole : doc.OldPole;
        var serial = Regex.Replace(pole?.SerialNumber ?? "", "[^A-Za-z0-9_-]+", "_").Trim('_', '-');
        if (serial.Length > 64) serial = serial[..64];
        if (serial.Length == 0) serial = doc.Kind == "pit" ? "groapa" : ModernWorkRules.SerialState(pole) switch
        {
            "missing" => "fara_serie", "damaged" => "eticheta_deteriorata", _ => "serie_necompletata"
        };
        var role = photo.Role switch
        {
            "old" => "vechi", "new" => "nou", "before" => "inainte", "after" => "remediat",
            "pole" => "stalp", "pit" => "groapa", "old_label" => "eticheta_veche",
            "new_label" => "eticheta_noua", "label" => "eticheta", _ => "foto"
        };
        var extension = original && photo.OriginalMimeType == "image/png" ? "png" : "jpg";
        return $"photos/{serial}_{role}_{index + 1:D2}_{workId.ToString("N")[..8]}_{photo.Id:N}.{(original ? "original" : "stamped")}.{extension}";
    }
    public static string StatusLabel(string status, bool romanian) => status switch
    {
        "in_progress" => romanian ? "În lucru" : "In progress",
        "repaired" => romanian ? "Remediat" : "Repaired",
        "replaced" => romanian ? "Înlocuit" : "Replaced",
        "removed" => romanian ? "Desființat" : "Removed",
        _ => romanian ? "Groapă" : "Pit"
    };
    private static string SerialStateLabel(string state, bool romanian) => state switch
    {
        "present" => romanian ? "Serie confirmată" : "Serial confirmed",
        "missing" => romanian ? "Fără serie" : "No serial",
        "damaged" => romanian ? "Etichetă deteriorată" : "Damaged label",
        _ => romanian ? "Necompletat" : "Not recorded"
    };
    public static byte[] Csv(IEnumerable<WorkDetails> works, string kind, bool romanian)
    {
        var text = new StringBuilder();
        void Row(IEnumerable<string> cells) => text.AppendJoin(',', cells.Select(WorkExports.CsvCell)).Append("\r\n");
        string Number(double? value) => value?.ToString("F6", CultureInfo.InvariantCulture) ?? "";
        var replacement = kind == "replacement";
        var leadingHeaders = replacement
            ? romanian ? new[] { "Coordonata X vechi", "Coordonata Y vechi", "Coordonata X nou", "Coordonata Y nou", "S/N vechi", "S/N nou" }
                : ["Old latitude (X)", "Old longitude (Y)", "New latitude (X)", "New longitude (Y)", "Old S/N", "New S/N"]
            : romanian ? ["Coordonata X", "Coordonata Y", "S/N"] : ["Latitude (X)", "Longitude (Y)", "S/N"];
        var commonHeaders = romanian ? new[] { "Status", "Număr fotografii", "Fotografii" } : ["Status", "Photo count", "Photos"];
        var stateHeaders = replacement
            ? romanian ? new[] { "Stare serie veche", "Stare serie nouă" } : ["Old serial state", "New serial state"]
            : romanian ? ["Stare serie"] : ["Serial state"];
        Row([.. leadingHeaders, .. commonHeaders, .. stateHeaders,
            .. (romanian ? new[] { "Județe / localități", "Utilizator", "ID lucrare" } : ["Counties / localities", "Username", "Work ID"])]);
        foreach (var work in works)
        {
            var d = work.Document; var report = Data(work.Id, d);
            var leading = replacement
                ? new[] { Number(report.OldX), Number(report.OldY), Number(report.NewX), Number(report.NewY),
                    d.OldPole?.SerialNumber ?? "", d.NewPole?.SerialNumber ?? "" }
                : [Number(report.X), Number(report.Y), d.Kind == "pit" ? "" : d.OldPole?.SerialNumber ?? ""];
            var states = replacement ? new[] { SerialStateLabel(report.OldSerialState, romanian), SerialStateLabel(report.NewSerialState, romanian) }
                : [d.Kind == "pit" ? "" : SerialStateLabel(report.OldSerialState, romanian)];
            Row([.. leading, StatusLabel(report.Status, romanian), d.Photos.Length.ToString(CultureInfo.InvariantCulture),
                string.Join(" | ", report.PhotoNames), .. states,
                string.Join(" | ", d.Photos.Select(x => $"{x.Locality}, {x.County}").Distinct()), work.Owner, work.Id.ToString("D")]);
        }
        var encoding = new UTF8Encoding(true);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text.ToString())];
    }
}