using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GeoCapture.Api.Auth;
using GeoCapture.Api.FieldWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GeoCapture.Api.Tests;

public sealed class FieldWorkTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABAAAAAMCAIAAADkharWAAAAF0lEQVR4nGN0y5vGQApgIkn1qIYRpAEAER0BYmrANbUAAAAASUVORK5CYII=");
    private static readonly byte[] Jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAAMABADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDGooor2Dyz/9k=");

    private static WorkDocument Document(string kind = "replacement")
    {
        var now = DateTime.UtcNow.AddSeconds(-2);
        var roles = kind switch { "replacement" => new[] { "old", "new" }, "repair" => ["before", "after"], "removal" => ["pole"], _ => ["pit"] };
        var photos = roles.Select(role =>
        {
            var photo = new WorkPhotoDescriptor(Guid.NewGuid(), role, 46.777987, 24.309925, 4.5, now, now.AddSeconds(-1),
                120, "Sâncraiu", "Cluj", "manual", role == "old", "image/png", "");
            return photo with { OverlayText = WorkRules.Overlay(photo) };
        }).ToArray();
        return new(kind, kind == "pit" ? null : new PoleIdentity("0002610586", "T0PL0138RO", "manual", null),
            kind == "replacement" ? new PoleIdentity("0002610587", "T0PL0138RO", "manual", null) : null, "", photos);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, Guid id, WorkSaveRequest input, bool attachPhotos = true)
    {
        var csrf = (await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf"))!;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/field-work/{id}/revisions");
        request.Headers.Add(AuthSetup.CsrfHeader, csrf.Token);
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(WorkRules.Serialize(input)), "manifest");
        if (attachPhotos) foreach (var photo in input.Document.Photos)
        {
            content.Add(new ByteArrayContent(Png), $"original_{photo.Id:D}", "original.png");
            content.Add(new ByteArrayContent(Jpeg), $"stamped_{photo.Id:D}", "stamped.jpg");
        }
        request.Content = content;
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Post<T>(HttpClient client, string path, T value)
    {
        var csrf = (await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf"))!;
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(value) };
        request.Headers.Add(AuthSetup.CsrfHeader, csrf.Token);
        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("repair")]
    [InlineData("removal")]
    [InlineData("pit")]
    public async Task EachWorkflowStoresBothPhotoVariantsAndMetadata(string kind)
    {
        using var factory = new AuthApiFactory();
        await factory.SeedAsync(mustChangePassword: false);
        using var client = factory.NewClient();
        await UploadApiTests.Login(client);
        var id = Guid.NewGuid();
        var input = new WorkSaveRequest(Guid.NewGuid(), null, "Initial upload", Document(kind));
        using var response = await Send(client, id, input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<WorkDetails>())!;
        Assert.Equal(id, result.Id);
        Assert.Equal(kind, result.Document.Kind);
        Assert.Equal(1, result.Revision);
        Assert.Equal("Sâncraiu", result.Document.Photos[0].Locality);
        Assert.Contains("UTC+02:00", result.Document.Photos[0].OverlayText);
        if (kind != "pit") Assert.Equal("0002610586", result.Document.OldPole!.SerialNumber);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        Assert.Equal(input.Document.Photos.Length, await db.Photos.CountAsync());
        var photo = await db.Photos.FirstAsync();
        Assert.Equal(Png, photo.OriginalBytes);
        Assert.Equal(Jpeg, photo.StampedBytes);
        Assert.Equal(WorkRules.Hash(Png), photo.OriginalHash);
        Assert.Equal(Png, await client.GetByteArrayAsync($"/api/field-work/{id}/photos/{photo.Id}/original"));
    }

    [Theory]
    [InlineData("old")]
    [InlineData("before")]
    public void IncompletePhotoWorkflowCannotBeUploaded(string role)
    {
        var kind = role == "old" ? "replacement" : "repair";
        var doc = Document(kind);
        doc = doc with { Photos = doc.Photos.Where(photo => photo.Role != role).ToArray() };
        Assert.Equal("work_required_photo", Assert.Throws<WorkException>(() => WorkRules.Validate(doc, DateTime.UtcNow)).Code);
    }

    [Fact]
    public void OldPolePhotoRequiresExplicitSerialVisibilityConfirmation()
    {
        var doc = Document();
        doc.Photos[0] = doc.Photos[0] with { SerialVisible = false };
        Assert.Equal("work_serial_not_confirmed", Assert.Throws<WorkException>(() => WorkRules.Validate(doc, DateTime.UtcNow)).Code);
    }

    [Fact]
    public void LocationLabelAndCaptureTimestampMustMatchTheMeasuredMetadata()
    {
        var doc = Document();
        doc.Photos[0] = doc.Photos[0] with { Locality = "Different town" };
        Assert.Throws<WorkException>(() => WorkRules.Validate(doc, DateTime.UtcNow));
        var missing = Document();
        missing.Photos[0] = missing.Photos[0] with { County = "" };
        Assert.Throws<WorkException>(() => WorkRules.Validate(missing, DateTime.UtcNow));
    }

    [Fact]
    public async Task RetryDoesNotCreateDuplicateWorkOrRevisions()
    {
        using var factory = new AuthApiFactory();
        await factory.SeedAsync(mustChangePassword: false);
        using var client = factory.NewClient();
        await UploadApiTests.Login(client);
        var id = Guid.NewGuid();
        var input = new WorkSaveRequest(Guid.NewGuid(), null, "Initial upload", Document());
        using var first = await Send(client, id, input);
        using var repeated = await Send(client, id, input);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.True((await repeated.Content.ReadFromJsonAsync<WorkDetails>())!.Replayed);
        using var changed = await Send(client, id, input with { Reason = "Changed request" });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        Assert.Equal(1, await db.Records.CountAsync());
        Assert.Equal(1, await db.Revisions.CountAsync());
    }

    [Fact]
    public async Task EditingAppendsHistoryAndKeepsRemovedPhotoEvidence()
    {
        using var factory = new AuthApiFactory();
        await factory.SeedAsync(mustChangePassword: false);
        using var client = factory.NewClient();
        await UploadApiTests.Login(client);
        var id = Guid.NewGuid();
        var input = new WorkSaveRequest(Guid.NewGuid(), null, "Initial upload", Document("pit"));
        using var created = await Send(client, id, input);
        var first = (await created.Content.ReadFromJsonAsync<WorkDetails>())!;
        var oldPhoto = first.Document.Photos[0].Id;
        var next = new WorkSaveRequest(Guid.NewGuid(), first.Version, "Retook the pit photo", Document("pit") with { Notes = "Corrected notes" });
        using var updated = await Send(client, id, next);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var second = (await updated.Content.ReadFromJsonAsync<WorkDetails>())!;
        Assert.Equal(2, second.Revision);
        Assert.NotEqual(first.Version, second.Version);
        Assert.Equal(Png, await client.GetByteArrayAsync($"/api/field-work/{id}/photos/{oldPhoto}/original"));
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/field-work/{id}/history");
        var versions = history.GetProperty("items");
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Equal(AuthApiFactory.Username, versions[0].GetProperty("actor").GetString());
        Assert.Contains("photos", versions[0].GetProperty("changes").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(oldPhoto, versions[1].GetProperty("document").GetProperty("photos")[0].GetProperty("id").GetGuid());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Photos.CountAsync());
    }

    [Fact]
    public async Task ExistingImagesCanBeReusedButNeverOverwritten()
    {
        using var factory = new AuthApiFactory();
        await factory.SeedAsync(mustChangePassword: false);
        using var client = factory.NewClient();
        await UploadApiTests.Login(client);
        var id = Guid.NewGuid();
        using var created = await Send(client, id, new(Guid.NewGuid(), null, "Initial upload", Document()));
        var current = (await created.Content.ReadFromJsonAsync<WorkDetails>())!;
        var changed = new WorkSaveRequest(Guid.NewGuid(), current.Version, "Fixed serial", current.Document with
        { OldPole = current.Document.OldPole! with { SerialNumber = "0000009" } });
        using var updated = await Send(client, id, changed, attachPhotos: false);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var second = (await updated.Content.ReadFromJsonAsync<WorkDetails>())!;
        var altered = second.Document.Photos.Select(photo => photo with { Locality = "Another place" }).ToArray();
        altered = altered.Select(photo => photo with { OverlayText = WorkRules.Overlay(photo) }).ToArray();
        using var invalid = await Send(client, id, new(Guid.NewGuid(), second.Version, "Bad overwrite", second.Document with { Photos = altered }));
        Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Revisions.CountAsync());
    }

    [Fact]
    public async Task StaleVersionCannotOverwriteAnotherEdit()
    {
        using var factory = new AuthApiFactory();
        await factory.SeedAsync(mustChangePassword: false);
        using var client = factory.NewClient();
        await UploadApiTests.Login(client);
        var id = Guid.NewGuid();
        using var created = await Send(client, id, new(Guid.NewGuid(), null, "Initial upload", Document("removal")));
        var first = (await created.Content.ReadFromJsonAsync<WorkDetails>())!;
        var edit = new WorkSaveRequest(Guid.NewGuid(), first.Version, "First correction", first.Document with { Notes = "First correction" });
        using var accepted = await Send(client, id, edit, false);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var stale = await Send(client, id, edit with { ChangeId = Guid.NewGuid(), Reason = "Stale correction" }, false);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task OnlyAdminCanEnableGenerationAndDisabledModeIsEnforcedServerSide()
    {
        using var factory = new AuthApiFactory();
        await factory.SeedAsync(mustChangePassword: false);
        using var client = factory.NewClient();
        await UploadApiTests.Login(client);
        var settings = (await client.GetFromJsonAsync<WorkSettingsResponse>("/api/field-work/settings"))!;
        Assert.False(settings.AllowQrGeneration);
        using var forbidden = await Post(client, "/api/field-work/settings", new WorkSettingsRequest(true, settings.Version));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var doc = Document();
        var pole = doc.NewPole! with { Source = "generated" };
        pole = pole with { RawCode = WorkRules.GeneratedQr(pole) };
        doc = doc with { NewPole = pole };
        using var rejected = await Send(client, Guid.NewGuid(), new(Guid.NewGuid(), null, "Initial upload", doc));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("work_qr_generation_disabled", (await rejected.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task AdminSwitchAllowsGenerationAndRejectsStaleSettings()
    {
        using var factory = new AuthApiFactory();
        await factory.SeedAsync(admin: true, mustChangePassword: false);
        using var client = factory.NewClient();
        await UploadApiTests.Login(client);
        using var enabled = await Post(client, "/api/field-work/settings", new WorkSettingsRequest(true, Guid.Empty));
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        using var stale = await Post(client, "/api/field-work/settings", new WorkSettingsRequest(false, Guid.Empty));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var doc = Document();
        var generated = doc.NewPole! with { Source = "generated" };
        generated = generated with { RawCode = WorkRules.GeneratedQr(generated) };
        using var accepted = await Send(client, Guid.NewGuid(), new(Guid.NewGuid(), null, "Initial upload", doc with { NewPole = generated }));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhotosHistoryAndEditsRespectOwnershipOrAdministratorRole(bool admin)
    {
        using var factory = new AuthApiFactory();
        await factory.SeedAsync(admin: admin, mustChangePassword: false);
        using var client = factory.NewClient();
        await UploadApiTests.Login(client);
        var id = Guid.NewGuid();
        using var created = await Send(client, id, new(Guid.NewGuid(), null, "Initial upload", Document("pit")));
        var work = (await created.Content.ReadFromJsonAsync<WorkDetails>())!;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
            (await db.Records.SingleAsync()).OwnerId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }
        foreach (var path in new[] { $"/api/field-work/{id}", $"/api/field-work/{id}/history",
            $"/api/field-work/{id}/photos/{work.Document.Photos[0].Id}/original" })
            Assert.Equal(admin ? HttpStatusCode.OK : HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        using var changed = await Send(client, id, new(Guid.NewGuid(), work.Version, "Changed by actor", work.Document), false);
        Assert.Equal(admin ? HttpStatusCode.OK : HttpStatusCode.NotFound, changed.StatusCode);
    }

    [Fact]
    public async Task AnonymousAndFirstLoginUsersCannotAccessWork()
    {
        using var factory = new AuthApiFactory();
        await factory.SeedAsync();
        using var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/field-work")).StatusCode);
        await UploadApiTests.Login(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/field-work")).StatusCode);
    }

    [Fact]
    public void MigrationMatchesTheModelAndKeepsTheExistingPhotoTablesSeparate()
    {
        using var db = new WorkDbContext(new DbContextOptionsBuilder<WorkDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused",
                options => options.MigrationsHistoryTable("__FieldWorkMigrationsHistory", "geo_capture")).Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = db.GetService<IMigrator>().GenerateScript();
        Assert.Contains("FieldWorkRevisions", sql);
        Assert.Contains("FieldWorkPhotos", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
        Assert.DoesNotContain("AspNetUsers", sql);
    }

    [Fact]
    public async Task DisablingGenerationStillAllowsCorrectingAnExistingGeneratedWork()
    {
        using var factory = new AuthApiFactory();
        await factory.SeedAsync(admin: true, mustChangePassword: false);
        using var client = factory.NewClient();
        await UploadApiTests.Login(client);
        using var enabled = await Post(client, "/api/field-work/settings", new WorkSettingsRequest(true, Guid.Empty));
        var settings = (await enabled.Content.ReadFromJsonAsync<WorkSettingsResponse>())!;
        var doc = Document();
        var pole = doc.NewPole! with { Source = "generated" };
        doc = doc with { NewPole = pole with { RawCode = WorkRules.GeneratedQr(pole) } };
        var id = Guid.NewGuid();
        using var created = await Send(client, id, new(Guid.NewGuid(), null, "Initial upload", doc));
        var work = (await created.Content.ReadFromJsonAsync<WorkDetails>())!;
        using var disabled = await Post(client, "/api/field-work/settings", new WorkSettingsRequest(false, settings.Version));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        using var corrected = await Send(client, id, new(Guid.NewGuid(), work.Version, "Corrected notes",
            work.Document with { Notes = "Changed without generating a new code" }), false);
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
    }

    [Fact]
    public void NullPhotoAndMissingNewNtetAreRejectedAsInputErrors()
    {
        var doc = Document();
        Assert.Throws<WorkException>(() => WorkRules.Validate(doc with { NewPole = doc.NewPole! with { NtetCode = "" } }, DateTime.UtcNow));
        doc.Photos[0] = null!;
        Assert.Throws<WorkException>(() => WorkRules.Validate(doc, DateTime.UtcNow));
    }

    [Fact]
    public async Task RevisionWritesRequireCsrf()
    {
        using var factory = new AuthApiFactory();
        await factory.SeedAsync(mustChangePassword: false);
        using var client = factory.NewClient();
        await UploadApiTests.Login(client);
        using var response = await client.PostAsync($"/api/field-work/{Guid.NewGuid()}/revisions", new StringContent("missing CSRF"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Records.CountAsync());
    }
}