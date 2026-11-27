using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GeoCapture.Api.FieldWork;

public sealed record RomaniaCounty(string Code, string Name);
public sealed record RomaniaLocality(string Code, string CountyCode, string Name, string Unit);

public sealed class RomaniaCatalog
{
    public IReadOnlyDictionary<string, RomaniaCounty> Counties { get; }
    public IReadOnlyDictionary<string, RomaniaLocality> Localities { get; }
    public RomaniaCatalog(string path) : this(File.ReadAllText(path), true) { }
    private RomaniaCatalog(string json, bool parsed)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("format").GetString() != "geocapture.romania-places" ||
            document.RootElement.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("Invalid Romania locality catalogue.");
        Counties = JsonSerializer.Deserialize<RomaniaCounty[]>(document.RootElement.GetProperty("counties"), WorkRules.Json)!
            .ToDictionary(x => x.Code);
        Localities = JsonSerializer.Deserialize<RomaniaLocality[]>(document.RootElement.GetProperty("localities"), WorkRules.Json)!
            .ToDictionary(x => x.Code);
        if (Counties.Count == 0 || Localities.Values.Any(x => !Counties.ContainsKey(x.CountyCode)))
            throw new InvalidDataException("Invalid Romania county links.");
    }
    public static RomaniaCatalog FromJson(string json) => new(json, true);
    public static RomaniaCatalog Load(string contentRoot)
    {
        var relative = Path.Combine("Data", "Romania", "romania_places.json");
        var path = Path.Combine(contentRoot, relative);
        return new RomaniaCatalog(File.Exists(path) ? path : Path.Combine(AppContext.BaseDirectory, relative));
    }

    public static string Fold(string value)
    {
        var text = value.Replace('ș', 's').Replace('ş', 's').Replace('Ș', 'S').Replace('Ş', 'S')
            .Replace('ț', 't').Replace('ţ', 't').Replace('Ț', 'T').Replace('Ţ', 'T');
        var result = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                result.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        return string.Join(' ', result.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public void ValidatePhoto(WorkPhotoDescriptor photo)
    {
        if (photo.CountyCode is null && photo.LocalityCode is null) return;
        if (photo.CountyCode is null || !Counties.TryGetValue(photo.CountyCode, out var county) ||
            Fold(county.Name) != Fold(photo.County)) throw new WorkException("work_invalid_place");
        if (photo.LocalityCode is not null &&
            (!Localities.TryGetValue(photo.LocalityCode, out var locality) ||
                locality.CountyCode != county.Code || Fold(locality.Name) != Fold(photo.Locality)))
            throw new WorkException("work_invalid_place");
    }
}