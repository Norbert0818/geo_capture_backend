using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GeoCapture.Api.Auth;
using GeoCapture.Api.FieldWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static GeoCapture.Api.Tests.ModernWorkTestData;

namespace GeoCapture.Api.Tests;

public sealed class ModernFieldWorkTests
{
    [Theory]
    [InlineData("replacement")]
    [InlineData("repair")]
    [InlineData("removal")]
    [InlineData("pit")]
    public void ModernWorkAcceptsItsOverviewPhotosWithoutSerialVisibilityCheckbox(string kind) =>
        WorkRules.Validate(Document(kind), DateTime.UtcNow);

    [Theory]
    [InlineData("", "missing")]
    [InlineData("", "damaged")]
    [InlineData("0000007", "damaged")]
    [InlineData("0", "present")]
    public void ExplicitSerialDecisionsDoNotRequireAnNtetCode(string serial, string state)
    {
        WorkRules.Validate(Document("repair") with { OldPole = Pole(serial, state) }, DateTime.UtcNow);
    }

    [Theory]
    [InlineData("A123")]
    [InlineData("12 34")]
    [InlineData("+123")]
    [InlineData("１２３")]
    [InlineData("١٢٣")]
    [InlineData("123\n")]
    [InlineData("")]
    public void AConfirmedPresentSerialMustContainOnlyAsciiDigits(string serial) =>
        Assert.Equal("work_serial_decision_required", Assert.Throws<WorkException>(() =>
            WorkRules.Validate(Document("repair") with { OldPole = Pole(serial) }, DateTime.UtcNow)).Code);

    [Fact]
    public void MissingCannotContainANumberAndUnconfirmedCannotBeCompleted()
    {
        foreach (var pole in new[] { Pole("12", "missing"), Pole() with { Confirmed = false }, Pole() with { SerialState = null } })
            Assert.Throws<WorkException>(() => WorkRules.Validate(Document("repair") with { OldPole = pole }, DateTime.UtcNow));
    }

    [Fact]
    public void InProgressCanContainOnlyOldPoleAndAnUndecidedSerial()
    {
        var doc = Document() with { Progress = "in_progress", OldPole = null, NewPole = null };
        doc = doc with { Photos = [doc.Photos[0]] };
        WorkRules.Validate(doc, DateTime.UtcNow);
        Assert.Throws<WorkException>(() => WorkRules.Validate(doc with { Progress = "completed" }, DateTime.UtcNow));
        WorkRules.Validate(doc with { OldPole = new("", "", "manual", null, "pending", false) }, DateTime.UtcNow);
        Assert.Throws<WorkException>(() => WorkRules.Validate(doc with { Photos = [] }, DateTime.UtcNow));
    }

    [Fact]
    public void LabelPhotosCannotReplaceOverviewEvidenceOrCrossTheOldNewIdentity()
    {
        var doc = Document();
        var label = Photo("old_label");
        doc = doc with { Photos = [.. doc.Photos, label], OldPole = doc.OldPole! with { EvidencePhotoId = label.Id } };
        WorkRules.Validate(doc, DateTime.UtcNow);
        Assert.Equal("work_invalid_serial_evidence", Assert.Throws<WorkException>(() =>
            WorkRules.Validate(doc with { NewPole = doc.NewPole! with { EvidencePhotoId = label.Id } }, DateTime.UtcNow)).Code);
        Assert.Throws<WorkException>(() => WorkRules.Validate(doc with { Photos = [label] }, DateTime.UtcNow));
    }

    [Fact]
    public void GeneratedQrStillRequiresNewPoleDigitsAndNtet()
    {
        var doc = Document(); var pole = doc.NewPole! with { NtetCode = "T0PL0138RO", Source = "generated" };
        pole = pole with { RawCode = WorkRules.GeneratedQr(pole) };
        WorkRules.Validate(doc with { NewPole = pole }, DateTime.UtcNow);
        foreach (var invalid in new[] { pole with { NtetCode = "" }, pole with { RawCode = "wrong" }, pole with { SerialState = "damaged" } })
            Assert.Throws<WorkException>(() => WorkRules.Validate(doc with { NewPole = invalid }, DateTime.UtcNow));
        Assert.Throws<WorkException>(() => WorkRules.Validate(doc with { OldPole = pole }, DateTime.UtcNow));
    }

    [Fact]
    public void LegacyJsonStillHasNoNewNullProperties()
    {
        var legacy = Document() with { SchemaVersion = null, Progress = null,
            OldPole = new("00012", "", "manual", null), NewPole = new("00013", "N", "manual", null),
            Photos = Document().Photos.Select(p => p with { CountyCode = null, LocalityCode = null }).ToArray() };
        var json = WorkRules.Serialize(legacy);
        foreach (var name in new[] { "schemaVersion", "progress", "serialState", "confirmed", "evidencePhotoId", "countyCode", "localityCode" })
            Assert.DoesNotContain($"\"{name}\"", json);
        Assert.Equal(json, WorkRules.Serialize(WorkRules.Read<WorkDocument>(json)));
        Assert.Throws<WorkException>(() => WorkRules.Validate(Document() with { SchemaVersion = 999 }, DateTime.UtcNow));
    }

    [Fact]
    public async Task OldPoleCanBeUploadedTodayAndNewPoleAddedLaterWithHistoryAndRetrySafety()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        var id = Guid.NewGuid(); var doc = Document();
        var initial = new WorkSaveRequest(Guid.NewGuid(), null, "Initial upload",
            doc with { Progress = "in_progress", NewPole = null, Photos = [doc.Photos[0]] });
        using var first = await Send(client, id, initial);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var saved = (await first.Content.ReadFromJsonAsync<WorkDetails>())!;
        var next = new WorkSaveRequest(Guid.NewGuid(), saved.Version, "Continue work", doc);
        using var second = await Send(client, id, next, [doc.Photos[1]]);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var complete = (await second.Content.ReadFromJsonAsync<WorkDetails>())!;
        Assert.Equal(2, complete.Revision); Assert.Equal("completed", complete.Document.Progress);
        using var retry = await Send(client, id, next, Array.Empty<WorkPhotoDescriptor>());
        Assert.True((await retry.Content.ReadFromJsonAsync<WorkDetails>())!.Replayed);
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/field-work/{id}/history");
        Assert.Equal(2, history.GetProperty("items").GetArrayLength());
        Assert.Equal("in_progress", history.GetProperty("items")[1].GetProperty("document").GetProperty("progress").GetString());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Photos.CountAsync());
        Assert.Equal(Png, await client.GetByteArrayAsync($"/api/field-work/{id}/photos/{doc.Photos[0].Id}/original"));
    }

    [Fact]
    public async Task RepairWithoutASerialCanBeCorrectedAfterUploadWithoutChangingExistingPhotoBytes()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        var id = Guid.NewGuid(); var doc = Document("repair") with { OldPole = Pole("", "damaged") };
        using var created = await Send(client, id, new(Guid.NewGuid(), null, "Initial upload", doc));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var saved = (await created.Content.ReadFromJsonAsync<WorkDetails>())!;
        var changed = doc with { OldPole = Pole("0000012", "damaged") };
        using var updated = await Send(client, id, new(Guid.NewGuid(), saved.Version, "Number found in records", changed), Array.Empty<WorkPhotoDescriptor>());
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var next = (await updated.Content.ReadFromJsonAsync<WorkDetails>())!;
        Assert.Equal("0000012", next.Document.OldPole!.SerialNumber);
        Assert.Equal(2, next.Revision);
        Assert.Equal(Jpeg, await client.GetByteArrayAsync($"/api/field-work/{id}/photos/{doc.Photos[0].Id}/stamped"));
    }

    [Fact]
    public async Task AnInvalidCatalogueLinkIsRejectedBeforeSavingAnyRevision()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        var doc = Document("removal"); var photo = doc.Photos[0] with { CountyCode = "31" };
        using var result = await Send(client, Guid.NewGuid(), new(Guid.NewGuid(), null, "Initial upload", doc with { Photos = [photo] }));
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("work_invalid_place", await result.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Revisions.CountAsync());
    }

    [Fact]
    public async Task DisabledGenerationAllowsAddingLabelEvidenceButRejectsChangingTheGeneratedQrPayload()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(admin: true, mustChangePassword: false);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        async Task<HttpResponseMessage> Settings(bool enabled, Guid version)
        {
            var csrf = (await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf"))!;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/field-work/settings")
                { Content = JsonContent.Create(new WorkSettingsRequest(enabled, version)) };
            request.Headers.Add(AuthSetup.CsrfHeader, csrf.Token);
            return await client.SendAsync(request);
        }
        using var enabled = await Settings(true, Guid.Empty);
        var setting = (await enabled.Content.ReadFromJsonAsync<WorkSettingsResponse>())!;
        var document = Document();
        var pole = document.NewPole! with { Source = "generated", NtetCode = "T0PL0138RO" };
        pole = pole with { RawCode = WorkRules.GeneratedQr(pole) };
        document = document with { NewPole = pole };
        var id = Guid.NewGuid();
        using var created = await Send(client, id, new(Guid.NewGuid(), null, "Initial upload", document));
        var first = (await created.Content.ReadFromJsonAsync<WorkDetails>())!;
        using var disabled = await Settings(false, setting.Version);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var label = Photo("new_label");
        document = document with { NewPole = pole with { EvidencePhotoId = label.Id }, Photos = [.. document.Photos, label] };
        using var evidence = await Send(client, id, new(Guid.NewGuid(), first.Version, "Add label evidence", document), [label]);
        Assert.Equal(HttpStatusCode.OK, evidence.StatusCode);
        var second = (await evidence.Content.ReadFromJsonAsync<WorkDetails>())!;
        pole = document.NewPole! with { SerialNumber = "0009999" };
        pole = pole with { RawCode = WorkRules.GeneratedQr(pole) };
        using var rejected = await Send(client, id, new(Guid.NewGuid(), second.Version, "Change generated code", document with { NewPole = pole }), Array.Empty<WorkPhotoDescriptor>());
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("work_qr_generation_disabled", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public void FullCatalogueContainsRomaniaAndDisambiguatesIdenticalLocalityNames()
    {
        var catalogue = RomaniaCatalog.Load(AppContext.BaseDirectory);
        Assert.Equal(42, catalogue.Counties.Count); Assert.Equal(13755, catalogue.Localities.Count);
        Assert.Equal("Cluj", catalogue.Counties["12"].Name);
        Assert.Equal("Călărași", catalogue.Counties["51"].Name);
        Assert.Equal("Giurgiu", catalogue.Counties["52"].Name);
        var locality = catalogue.Localities["59443"]; Assert.Equal("Sâncraiu", locality.Name);
        catalogue.ValidatePhoto(Photo());
        Assert.Throws<WorkException>(() => catalogue.ValidatePhoto(Photo() with { LocalityCode = "99999999" }));
        Assert.Throws<WorkException>(() => catalogue.ValidatePhoto(Photo() with { CountyCode = null }));
        catalogue.ValidatePhoto(Photo() with { LocalityCode = null, Locality = "A newly named village" });
        Assert.Contains(catalogue.Localities.Values.GroupBy(x => (x.CountyCode, x.Name)), x => x.Count() > 1 && x.Select(v => v.Unit).Distinct().Count() > 1);
    }
}