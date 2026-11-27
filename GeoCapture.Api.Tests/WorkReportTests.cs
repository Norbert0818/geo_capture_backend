using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GeoCapture.Api.FieldWork;
using Xunit;
using static GeoCapture.Api.Tests.ModernWorkTestData;

namespace GeoCapture.Api.Tests;

public sealed class WorkReportTests
{
    private static async Task<Guid[]> Ids(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/field-work?all=true&" + query))
        .GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task ReportsRequireAnAdministrator(bool admin, HttpStatusCode status)
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(admin: admin, mustChangePassword: false);
        using var client = factory.NewClient();
        using var anonymous = await client.GetAsync("/api/field-work/reports.csv?kind=repair");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await UploadApiTests.Login(client);
        using var response = await client.GetAsync("/api/field-work/reports.csv?kind=repair&all=true");
        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task CountyReturnsAllItsLocalitiesAndExactLocalityCodesNarrowTheResult()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(admin: true, mustChangePassword: false);
        var catalogue = RomaniaCatalog.Load(AppContext.BaseDirectory);
        var different = catalogue.Localities.Values.First(x => x.CountyCode == "12" && x.Code != "59443");
        var first = await Seed(factory, Document("repair"));
        var doc = Document("repair") with { Photos = [Photo("before", different.Code, different.Name), Photo("after", different.Code, different.Name)] };
        var second = await Seed(factory, doc);
        var third = catalogue.Localities.Values.First(x => x.CountyCode == "31");
        await Seed(factory, Document("repair") with { Photos = [Photo("after", third.Code, third.Name, "31", "Sălaj")] });
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        Assert.True(new[] { first, second }.ToHashSet().SetEquals(await Ids(client, "countyCode=12")));
        Assert.Equal(new[] { first }, await Ids(client, "countyCode=12&localityCode=59443"));
        var response = await client.GetFromJsonAsync<JsonElement>("/api/field-work?all=true&localityCode=59443");
        Assert.Equal("repaired", response.GetProperty("items")[0].GetProperty("report").GetProperty("status").GetString());
    }

    [Fact]
    public async Task SameNameVillagesWithinOneCountyAreDistinguishedBySirutaCode()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(admin: true, mustChangePassword: false);
        var catalogue = RomaniaCatalog.Load(AppContext.BaseDirectory);
        var pair = catalogue.Localities.Values.GroupBy(x => (x.CountyCode, x.Name)).First(x => x.Select(v => v.Unit).Distinct().Count() > 1).Take(2).ToArray();
        var ids = new List<Guid>();
        foreach (var place in pair)
            ids.Add(await Seed(factory, Document("removal") with { Photos = [Photo("pole", place.Code, place.Name, place.CountyCode, catalogue.Counties[place.CountyCode].Name)] }));
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        Assert.Equal(new[] { ids[0] }, await Ids(client, $"localityCode={pair[0].Code}"));
        Assert.Equal(new[] { ids[1] }, await Ids(client, $"localityCode={pair[1].Code}"));
    }

    [Fact]
    public async Task LegacyPlaceFallbackMatchesTheWholeNameAndRequiresTheSamePhoto()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(admin: true, mustChangePassword: false);
        WorkPhotoDescriptor Legacy(string town, string county) => Photo("pole", locality: town, county: county) with { CountyCode = null, LocalityCode = null };
        var match = await Seed(factory, Document("removal") with { Photos = [Legacy("Sancraiu", "Cluj")] });
        await Seed(factory, Document("removal") with { Photos = [Legacy("Sâncraiu de Mureș", "Cluj")] });
        await Seed(factory, Document("removal") with { Photos = [Legacy("Sâncraiu", "Sălaj"), Legacy("Other", "Cluj")] });
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        Assert.Equal(new[] { match }, await Ids(client, "countyCode=12&localityCode=59443"));
    }

    [Theory]
    [InlineData("countyCode=00")]
    [InlineData("countyCode=31&localityCode=59443")]
    [InlineData("countyCode=12&county=S%C4%83laj")]
    [InlineData("localityCode=99999999")]
    [InlineData("state=anything")]
    [InlineData("state=completed&state=in_progress")]
    public async Task InvalidCountyLocalityAndStateFiltersAreRejected(string query)
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(admin: true, mustChangePassword: false);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        foreach (var route in new[] { "/api/field-work?all=true&", "/api/field-work/reports.csv?kind=repair&all=true&" })
        {
            using var result = await client.GetAsync(route + query);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }
    }

    [Fact]
    public async Task CompletionFilterIncludesLegacyWorkAndExcludesPartialWork()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(admin: true, mustChangePassword: false);
        var completed = await Seed(factory, Document("repair"));
        var legacy = await Seed(factory, Document("repair") with { SchemaVersion = null, Progress = null });
        var partial = await Seed(factory, Document("repair", "in_progress"));
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        Assert.True(new[] { completed, legacy }.ToHashSet().SetEquals(await Ids(client, "state=completed")));
        Assert.Equal(new[] { partial }, await Ids(client, "state=in_progress"));
    }

    [Theory]
    [InlineData("repair", false)]
    [InlineData("repair", true)]
    [InlineData("replacement", false)]
    [InlineData("replacement", true)]
    public void ReportUsesRequestedColumnOrderAndPreservesRawSerialDigits(string kind, bool romanian)
    {
        var doc = Document(kind); var id = Guid.NewGuid();
        var work = new WorkDetails(id, Guid.NewGuid(), "worker", 2, Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, doc);
        var bytes = WorkReports.Csv([work], kind, romanian);
        Assert.Equal(new byte[] { 239, 187, 191 }, bytes.Take(3));
        var rows = Csv(bytes);
        Assert.Equal(2, rows.Count); Assert.Equal(rows[0].Length, rows[1].Length);
        Assert.Equal(romanian ? "Coordonata X" + (kind == "replacement" ? " vechi" : "") : kind == "replacement" ? "Old latitude (X)" : "Latitude (X)", rows[0][0]);
        Assert.Equal("46.777987", rows[1][0]); Assert.Equal("24.309925", rows[1][1]);
        Assert.Equal("0002610586", Cell(rows, 1, kind == "replacement" ? romanian ? "S/N vechi" : "Old S/N" : "S/N"));
        Assert.Equal(romanian ? kind == "repair" ? "Remediat" : "Înlocuit" : kind == "repair" ? "Repaired" : "Replaced", Cell(rows, 1, "Status"));
        Assert.Contains("0002610586_", Cell(rows, 1, romanian ? "Fotografii" : "Photos"));
    }

    [Fact]
    public void MissingOrDamagedSerialIsExplicitWithoutPollutingTheSnColumn()
    {
        var doc = Document() with { OldPole = Pole("", "missing"), NewPole = Pole("00012", "damaged") };
        var work = new WorkDetails(Guid.NewGuid(), Guid.NewGuid(), "=worker", 1, Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, doc);
        var rows = Csv(WorkReports.Csv([work], "replacement", true));
        Assert.Equal("", Cell(rows, 1, "S/N vechi")); Assert.Equal("00012", Cell(rows, 1, "S/N nou"));
        Assert.Equal("Fără serie", Cell(rows, 1, "Stare serie veche"));
        Assert.Equal("Etichetă deteriorată", Cell(rows, 1, "Stare serie nouă"));
        Assert.Equal("\t=worker", Cell(rows, 1, "Utilizator"));
    }

    [Fact]
    public void CoordinatesUseOverviewRatherThanCloseUpLabelAndPartialNewCoordinatesStayEmpty()
    {
        var doc = Document(); var label = Photo("old_label") with { Latitude = 40.1, Longitude = 20.2, CapturedAtUtc = DateTime.UtcNow.AddHours(-1) };
        doc = doc with { Photos = [label, .. doc.Photos] };
        var report = WorkReports.Data(Guid.NewGuid(), doc);
        Assert.Equal(46.777987, report.OldX); Assert.Equal(24.309925, report.OldY);
        report = WorkReports.Data(Guid.NewGuid(), doc with { Progress = "in_progress", Photos = [doc.Photos[1]] });
        Assert.Null(report.NewX); Assert.Null(report.NewY); Assert.Equal("in_progress", report.Status);
    }

    [Fact]
    public async Task ReportExportsEveryFilteredWorkOnceBeyondTheVisiblePageAndOnlyTheCurrentSnapshot()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(admin: true, mustChangePassword: false);
        await Seed(factory, Document("repair"), count: 25, previous: Document("repair") with { OldPole = Pole("99999") });
        await Seed(factory, Document("repair"), other: true, count: 2);
        await Seed(factory, Document("repair", "in_progress"));
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        using var result = await client.GetAsync("/api/field-work/reports.csv?kind=repair&all=true&state=completed&countyCode=12&page=2&lang=ro");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Contains("no-store", result.Headers.CacheControl!.ToString());
        var rows = Csv(await result.Content.ReadAsByteArrayAsync());
        Assert.Equal(28, rows.Count); Assert.All(rows.Skip(1), row => Assert.Equal("0002610586", row[2]));
        Assert.Single(Csv(await client.GetByteArrayAsync("/api/field-work/reports.csv?kind=repair&all=true&serial=99999")));
    }

    [Fact]
    public async Task SerialBasedNamesMatchZipEntriesAndRetainOriginalHashes()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(admin: true, mustChangePassword: false);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        var id = Guid.NewGuid(); var doc = Document("repair");
        using var saved = await Send(client, id, new(Guid.NewGuid(), null, "Initial upload", doc));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(await client.GetByteArrayAsync($"/api/field-work/{id}/export.zip?revision=1")));
        var path = WorkReports.PhotoPath(id, doc, doc.Photos[0], 0, true);
        var entry = archive.GetEntry(path); Assert.NotNull(entry);
        using var bytes = new MemoryStream(); await entry.Open().CopyToAsync(bytes);
        Assert.Equal(Png, bytes.ToArray());
        Assert.Contains(WorkReports.PhotoPath(id, doc, doc.Photos[0], 0, false)[7..], WorkReports.Data(id, doc).PhotoNames);
    }

    [Fact]
    public void FilenamesCannotTraverseDirectoriesOrCollideOnRepeatedSerials()
    {
        var doc = Document("repair") with { OldPole = Pole("../../00012") };
        var id = Guid.NewGuid(); var names = doc.Photos.Select((p, i) => WorkReports.PhotoPath(id, doc, p, i, false)).ToArray();
        Assert.Equal(2, names.Distinct().Count()); Assert.All(names, name => { Assert.StartsWith("photos/00012_", name); Assert.DoesNotContain("..", name); });
    }
}