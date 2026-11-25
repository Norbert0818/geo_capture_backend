using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GeoCapture.Api.Data;
using GeoCapture.Api.FieldWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualBasic.FileIO;
using Xunit;

namespace GeoCapture.Api.Tests;

public sealed class FieldWorkExportTests
{
    private static readonly byte[] Original = [137, 80, 78, 71, 1, 2, 3];
    private static readonly byte[] Stamped = [255, 216, 255, 4, 5, 6];
    private static WorkDocument Document(string serial = "0002610586", string locality = "Sâncraiu", string kind = "replacement")
    {
        var time = new DateTime(2026, 11, 24, 23, 30, 0, DateTimeKind.Utc);
        var photo = new WorkPhotoDescriptor(Guid.NewGuid(), "old", -46.777987, 24.309925,
            4.5, time, time.AddSeconds(-1), 120, locality, "Cluj", "manual", true, "image/png", "");
        photo = photo with { OverlayText = WorkRules.Overlay(photo) };
        return new(kind, new(serial, "T0PL0138RO", "manual", null),
            new("0002610587", "T0PL0138RO", "manual", null), "", [photo]);
    }

    private static async Task<Guid> Seed(AuthApiFactory factory, WorkDocument document, bool other = false,
        WorkDocument? previous = null, int count = 1)
    {
        using var scope = factory.Services.CreateScope();
        var owner = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Select(x => x.Id).SingleAsync();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        var first = Guid.Empty;
        for (var index = 0; index < count; index++)
        {
            var id = Guid.NewGuid();
            if (index == 0) first = id;
            var now = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc).AddMinutes(index);
            var work = new FieldWorkRecord { Id = id, OwnerId = other ? Guid.NewGuid() : owner,
                OwnerName = other ? "other-worker" : AuthApiFactory.Username, CreatedAtUtc = now,
                UpdatedAtUtc = now, Version = Guid.NewGuid(), Revision = previous is null ? 1 : 2 };
            db.Records.Add(work);
            void Version(WorkDocument input, int number)
            {
                // Repeated seed records need unique photo IDs, as real captures do.
                var value = input with { Photos = input.Photos.Select(p => p with { Id = Guid.NewGuid() }).ToArray() };
                db.Revisions.Add(new FieldWorkRevision { Id = Guid.NewGuid(), WorkId = id, Number = number,
                    ChangeId = Guid.NewGuid(), ResultVersion = Guid.NewGuid(), ActorId = owner,
                    ActorName = work.OwnerName, AtUtc = now, Reason = $"Version {number}",
                    ChangesJson = WorkRules.Serialize(new[] { "photos" }), DocumentJson = WorkRules.Serialize(value) });
                foreach (var photo in value.Photos)
                    db.Photos.Add(new FieldWorkPhoto { Id = photo.Id, WorkId = id,
                        DescriptorJson = WorkRules.Serialize(photo), OriginalMimeType = "image/png",
                        OriginalBytes = Original, StampedBytes = Stamped,
                        OriginalHash = WorkRules.Hash(Original), StampedHash = WorkRules.Hash(Stamped) });
            }
            if (previous is not null) Version(previous, 1);
            Version(document, work.Revision);
        }
        await db.SaveChangesAsync();
        return first;
    }

    private static List<string[]> ParseCsv(byte[] bytes)
    {
        Assert.Equal(new byte[] { 239, 187, 191 }, bytes.Take(3));
        using var reader = new TextFieldParser(new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')))
            { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        reader.SetDelimiters(",");
        var rows = new List<string[]>();
        while (!reader.EndOfData) rows.Add(reader.ReadFields()!);
        return rows;
    }
    private static string Cell(List<string[]> rows, int row, string column) => rows[row][Array.IndexOf(rows[0], column)];

    [Theory]
    [InlineData("/api/field-work/export.csv")]
    [InlineData("/api/field-work/11111111-1111-4111-8111-111111111111/export.zip")]
    public async Task AnonymousExportIsRejected(string path)
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        using var client = factory.NewClient(); using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CsvExportsAllFilteredRowsBeyondTheVisiblePageAndOnlyOwnWork()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        await Seed(factory, Document(), count: 25); await Seed(factory, Document(), other: true, count: 3);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        using var response = await client.GetAsync("/api/field-work/export.csv?serial=10586&page=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var rows = ParseCsv(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(26, rows.Count); Assert.All(rows, row => Assert.Equal(23, row.Length));
        Assert.All(rows.Skip(1), row => Assert.Equal(AuthApiFactory.Username, row[3]));
        Assert.Equal("0002610586", Cell(rows, 1, "old_serial_number"));
        Assert.Equal("0002610587", Cell(rows, 1, "new_serial_number"));
        Assert.Equal("-46.777987", Cell(rows, 1, "latitude"));
        Assert.Equal("24.309925", Cell(rows, 1, "longitude"));
        Assert.Equal("2026-11-25T01:30:00+02:00", Cell(rows, 1, "captured_at_local"));
    }

    [Fact]
    public async Task CsvUsesCurrentSnapshotsAndTheSameCombinedFiltersAsTheList()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        var id = await Seed(factory, Document("CURRENT", "Sâncraiu"), previous: Document("ARCHIVED", "Old village"));
        await Seed(factory, Document("CURRENT", "Zalău"));
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        var rows = ParseCsv(await client.GetByteArrayAsync("/api/field-work/export.csv?serial=CURRENT&kind=replacement&locality=S%C3%A2ncraiu&county=Cluj&fromDate=2026-11-25&toDate=2026-11-25"));
        Assert.Equal(2, rows.Count); Assert.Equal(id.ToString("D"), Cell(rows, 1, "work_id"));
        Assert.Equal("2", Cell(rows, 1, "revision"));
        Assert.Single(ParseCsv(await client.GetByteArrayAsync("/api/field-work/export.csv?serial=ARCHIVED")));
    }

    [Fact]
    public async Task AdminCanExportOtherOwnersButNormalUsersCannotRequestThatScope()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(admin: true, mustChangePassword: false);
        await Seed(factory, Document()); await Seed(factory, Document(), other: true);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        var rows = ParseCsv(await client.GetByteArrayAsync("/api/field-work/export.csv?all=true&owner=OTHER-WORKER"));
        Assert.Equal(2, rows.Count); Assert.Equal("other-worker", Cell(rows, 1, "username"));
        using var invalid = await client.GetAsync("/api/field-work/export.csv?owner=other");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData("all=true")]
    [InlineData("owner=other")]
    public async Task AUserCannotBypassTheExportOwnerScope(string query)
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        using var response = await client.GetAsync("/api/field-work/export.csv?" + query);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CsvLimitRejectsTheWholeExportInsteadOfSilentlyTruncatingIt()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        await Seed(factory, Document(), count: WorkExports.MaxCsvWorks + 1);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        using var response = await client.GetAsync("/api/field-work/export.csv");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("work_export_too_many", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EmptyCsvIsAValidHeaderOnlyFileAndInvalidDatesAreRejected()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        Assert.Single(ParseCsv(await client.GetByteArrayAsync("/api/field-work/export.csv")));
        using var invalid = await client.GetAsync("/api/field-work/export.csv?fromDate=2026-02-30");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://invalid.test\")")]
    [InlineData(" +1+2")]
    [InlineData("-1+2")]
    [InlineData("@SUM(A1)")]
    [InlineData("\t=1+2")]
    [InlineData("＝1+2")]
    public void FormulaLookingTextIsGuardedWithoutChangingTheSourceValue(string value)
    {
        var document = Document(value, "Sâncraiu, \"Centru\"");
        var work = new WorkDetails(Guid.NewGuid(), Guid.NewGuid(), "worker", 1, Guid.NewGuid(),
            DateTime.UtcNow, DateTime.UtcNow, document);
        var rows = ParseCsv(WorkExports.Csv([work]));
        Assert.Equal("\t" + value, Cell(rows, 1, "old_serial_number"));
        Assert.Equal(value, document.OldPole!.SerialNumber);
        Assert.Equal("Sâncraiu, \"Centru\"", Cell(rows, 1, "locality"));
    }

    [Fact]
    public async Task ZipContainsBothPhotoVariantsMetadataAndCorrectRelativeCsvPaths()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        var id = await Seed(factory, Document());
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        using var response = await client.GetAsync($"/api/field-work/{id}/export.zip?revision=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("_v1.zip", response.Content.Headers.ContentDisposition!.ToString());
        using var buffer = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        Assert.Equal(4, zip.Entries.Count);
        using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        var manifest = await JsonDocument.ParseAsync(manifestStream);
        Assert.Equal("geocapture.work-export", manifest.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("work").GetProperty("revision").GetInt32());
        var files = manifest.RootElement.GetProperty("files")[0];
        async Task<byte[]> Bytes(string name) { using var input = zip.GetEntry(name)!.Open(); using var output = new MemoryStream(); await input.CopyToAsync(output); return output.ToArray(); }
        Assert.Equal(Original, await Bytes(files.GetProperty("original").GetString()!));
        Assert.Equal(Stamped, await Bytes(files.GetProperty("stamped").GetString()!));
        Assert.Equal(WorkRules.Hash(Original), files.GetProperty("originalHash").GetString());
        var rows = ParseCsv(await Bytes("photos.csv"));
        Assert.Equal(files.GetProperty("original").GetString(), Cell(rows, 1, "original_file"));
        Assert.Equal(files.GetProperty("stamped").GetString(), Cell(rows, 1, "stamped_file"));
        Assert.All(zip.Entries, entry => { Assert.DoesNotContain("..", entry.FullName); Assert.DoesNotContain('\\', entry.FullName); });
    }

    [Fact]
    public async Task ZipPinsTheSelectedHistoricalRevision()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        var id = await Seed(factory, Document("CURRENT"), previous: Document("PREVIOUS"));
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        foreach (var number in new[] { 1, 2 })
        {
            using var zip = new ZipArchive(new MemoryStream(await client.GetByteArrayAsync($"/api/field-work/{id}/export.zip?revision={number}")), ZipArchiveMode.Read);
            using var json = zip.GetEntry("manifest.json")!.Open();
            var manifest = await JsonDocument.ParseAsync(json);
            var work = manifest.RootElement.GetProperty("work");
            Assert.Equal(number, work.GetProperty("revision").GetInt32());
            Assert.Equal(number == 1 ? "PREVIOUS" : "CURRENT", work.GetProperty("document").GetProperty("oldPole").GetProperty("serialNumber").GetString());
        }
        using var invalid = await client.GetAsync($"/api/field-work/{id}/export.zip?revision=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var missing = await client.GetAsync($"/api/field-work/{id}/export.zip?revision=3");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ZipDoesNotExposeOtherUsersRecords()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        var id = await Seed(factory, Document(), other: true);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        using var response = await client.GetAsync($"/api/field-work/{id}/export.zip");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminCanExportAnotherUsersZip()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(admin: true, mustChangePassword: false);
        var id = await Seed(factory, Document(), other: true);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        using var response = await client.GetAsync($"/api/field-work/{id}/export.zip");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MissingPhotoIsRejectedBeforeTheZipResponseStarts()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        var id = await Seed(factory, Document());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
            db.Photos.RemoveRange(await db.Photos.ToArrayAsync()); await db.SaveChangesAsync();
        }
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        using var response = await client.GetAsync($"/api/field-work/{id}/export.zip");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FinishedZipIsSeekableAndTemporaryFileIsDeletedWhenClosed()
    {
        var document = Document();
        var work = new WorkDetails(Guid.NewGuid(), Guid.NewGuid(), "worker", 1, Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, document);
        var revision = new FieldWorkRevision { ChangesJson = "[]" };
        var infos = document.Photos.Select(p => new WorkExportPhotoInfo(p.Id, "image/png", WorkRules.Hash(Original), WorkRules.Hash(Stamped), Original.Length + Stamped.Length)).ToArray();
        var destination = await WorkExports.CreateZipAsync(work, revision, infos,
            (_, _) => Task.FromResult(new WorkExportPhotoContent(Original, Stamped)), DateTimeOffset.UtcNow, default);
        var path = destination.Name;
        Assert.True(destination.CanRead); Assert.True(destination.CanSeek); Assert.Equal(0, destination.Position);
        using (var zip = new ZipArchive(destination, ZipArchiveMode.Read, leaveOpen: true)) Assert.Equal(4, zip.Entries.Count);
        await destination.DisposeAsync();
        Assert.False(File.Exists(path));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkExports.CreateZipAsync(work,
            revision, infos, (_, _) => Task.FromResult(new WorkExportPhotoContent(Original, Stamped)), DateTimeOffset.UtcNow, cancelled.Token));
    }

    [Fact]
    public async Task FailedZipCreationRemovesItsTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"geocapture-export-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var document = Document();
            var work = new WorkDetails(Guid.NewGuid(), Guid.NewGuid(), "worker", 1, Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, document);
            var revision = new FieldWorkRevision { ChangesJson = "[]" };
            var infos = document.Photos.Select(p => new WorkExportPhotoInfo(p.Id, "image/png", "WRONG-HASH", WorkRules.Hash(Stamped), 10)).ToArray();
            await Assert.ThrowsAsync<InvalidDataException>(() => WorkExports.CreateZipAsync(work,
                revision, infos, (_, _) => Task.FromResult(new WorkExportPhotoContent(Original, Stamped)), DateTimeOffset.UtcNow, default, directory));
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}