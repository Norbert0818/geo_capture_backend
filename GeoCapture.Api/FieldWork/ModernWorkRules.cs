using System.Text.RegularExpressions;

namespace GeoCapture.Api.FieldWork;

public static class ModernWorkRules
{
    public static readonly string[] LabelRoles = ["old_label", "new_label", "label"];
    public static bool Digits(string? serial) => serial is not null &&
        Regex.IsMatch(serial, @"\A[0-9]{1,128}\z", RegexOptions.CultureInvariant);

    public static string[] OverviewRoles(string kind) => kind switch
    {
        "replacement" => ["old", "new"], "repair" => ["before", "after"],
        "removal" => ["pole"], "pit" => ["pit"],
        _ => throw new WorkException("work_invalid_manifest")
    };
    public static string[] AllowedRoles(string kind) => kind switch
    {
        "replacement" => ["old", "new", "old_label", "new_label"],
        "repair" => ["before", "after", "label"],
        "removal" => ["pole", "label"], "pit" => ["pit"],
        _ => throw new WorkException("work_invalid_manifest")
    };
    public static string SerialState(PoleIdentity? pole) => pole?.SerialState ??
        (string.IsNullOrEmpty(pole?.SerialNumber) ? "pending" : "present");
    public static string Progress(WorkDocument document) => document.Progress ?? "completed";

    private static void Identity(PoleIdentity? pole, bool pendingAllowed,
        bool isNew, WorkDocument document)
    {
        if (pole is null)
        {
            if (pendingAllowed) return;
            throw new WorkException("work_serial_decision_required");
        }
        if (pole.SerialNumber is null || pole.NtetCode is null || pole.NtetCode.Length > 128 ||
            pole.NtetCode != pole.NtetCode.Trim() || pole.NtetCode.Any(char.IsControl) ||
            pole.RawCode?.Length > 4096 || pole.Source is not ("manual" or "scanned" or "ocr" or "generated"))
            throw new WorkException("work_invalid_identity");
        var valid = pole.SerialState switch
        {
            "present" => Digits(pole.SerialNumber) && pole.Confirmed == true,
            "missing" => pole.SerialNumber.Length == 0 && pole.Confirmed == true,
            "damaged" => (pole.SerialNumber.Length == 0 || Digits(pole.SerialNumber)) && pole.Confirmed == true,
            "pending" => pendingAllowed && pole.SerialNumber.Length == 0 && pole.Confirmed != true,
            _ => false
        };
        if (!valid) throw new WorkException("work_serial_decision_required");
        if (pole.Source == "scanned" && string.IsNullOrWhiteSpace(pole.RawCode))
            throw new WorkException("work_invalid_identity");
        if (pole.Source == "generated" && (!isNew || pole.SerialState != "present" ||
            pole.NtetCode.Length == 0 || pole.RawCode != WorkRules.GeneratedQr(pole)))
            throw new WorkException("work_invalid_qr");
        if (pole.EvidencePhotoId is { } evidence)
        {
            var photo = document.Photos.SingleOrDefault(x => x.Id == evidence);
            var roles = isNew ? new[] { "new", "new_label" } : document.Kind switch
            {
                "replacement" => ["old", "old_label"], "repair" => ["before", "after", "label"],
                "removal" => ["pole", "label"], _ => Array.Empty<string>()
            };
            if (evidence == Guid.Empty || photo is null || !roles.Contains(photo.Role))
                throw new WorkException("work_invalid_serial_evidence");
        }
    }

    public static void Validate(WorkDocument document, DateTime now)
    {
        if (!WorkRules.Kinds.Contains(document.Kind) ||
            document.Progress is not ("in_progress" or "completed") ||
            document.Notes is null || document.Notes.Length > 4000 ||
            document.Photos is null || document.Photos.Length is < 1 or > WorkRules.MaxPhotos ||
            document.Photos.Any(x => x is null) ||
            document.Photos.Select(x => x.Id).Distinct().Count() != document.Photos.Length)
            throw new WorkException("work_invalid_manifest");
        var pending = document.Progress == "in_progress";
        var roles = AllowedRoles(document.Kind);
        if (document.Photos.Any(x => !roles.Contains(x.Role)) ||
            !pending && OverviewRoles(document.Kind).Any(role => !document.Photos.Any(x => x.Role == role)))
            throw new WorkException("work_required_photo");
        if (document.Kind == "pit")
        {
            if (document.OldPole is not null || document.NewPole is not null)
                throw new WorkException("work_invalid_identity");
        }
        else
        {
            Identity(document.OldPole, pending, false, document);
            if (document.Kind == "replacement") Identity(document.NewPole, pending, true, document);
            else if (document.NewPole is not null) throw new WorkException("work_invalid_identity");
        }
        foreach (var photo in document.Photos) WorkRules.ValidatePhoto(photo, now);
    }
}