using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GeoCapture.Api.Auth;
using GeoCapture.Api.Data;
using GeoCapture.Api.Models;
using GeoCapture.Api.Uploads;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GeoCapture.Api.Tests;

public sealed class UploadApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAMCAIAAADkharWAAAAF0lEQVR4nGN0y5vGQApgIkn1qIYRpAEAER0BYmrANbUAAAAASUVORK5CYII=");
    private static readonly byte[] Jpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAAMABADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDGooor2Dyz/9k=");

    [Fact]
    public async Task StoresBothFilesMetadataAndServerOwnership()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await Login(client);
        var manifest = Manifest(2, "  SERIAL / 123  ");

        using var response = await Send(client, manifest);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<UploadDetails>())!;

        Assert.Equal("SERIAL / 123", result.SerialNumber);
        Assert.Equal("OLD-0001", result.OldSerialNumber);
        Assert.Equal("SERIAL / 123", result.NewSerialNumber);
        Assert.Equal(manifest.Id, result.ClientUploadId);
        Assert.Equal(AuthApiFactory.Username, result.UploadedByUsername);
        Assert.False(result.Replayed);
        Assert.NotEqual(Guid.Empty, result.Version);
        Assert.Equal(2, result.Photos.Length);
        Assert.Equal(manifest.Photos![0]!.Id, result.Photos[0].ClientPhotoId);
        Assert.Equal("47.181912, 23.165123", result.Photos[0].CoordinatesText);
        Assert.Equal(5, result.Photos[0].AccuracyMeters);
        Assert.Equal(manifest.Photos[0]!.CapturedAtUtc.UtcDateTime, result.Photos[0].CapturedAtUtc);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var upload = await database.PhotoUploads.SingleAsync();
        Assert.Equal(upload.OwnerId, result.OwnerId);
        Assert.Equal(2, await database.PhotoContents.CountAsync());
        var photo = await database.CapturedPhotos.Include(x => x.Content)
            .SingleAsync(x => x.Position == 0);
        Assert.Equal(Png, photo.Content.OriginalBytes);
        Assert.Equal(Jpeg, photo.Content.StampedBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Png)), photo.OriginalSha256);
        Assert.Equal(16, photo.OriginalWidth);
        Assert.Equal(12, photo.OriginalHeight);
        Assert.Equal(Png.Length, photo.OriginalByteLength);
        Assert.Equal(DateTimeKind.Utc, photo.CapturedAtUtc.Kind);

        using var image = await client.GetAsync(result.Photos[0].OriginalUrl);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await image.Content.ReadAsByteArrayAsync());
        using var stamped = await client.GetAsync(result.Photos[0].StampedUrl);
        Assert.Equal(Jpeg, await stamped.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task IdenticalRetryReturnsTheExistingUpload()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await Login(client);
        var manifest = Manifest();

        using var first = await Send(client, manifest);
        using var second = await Send(client, manifest);
        var one = (await first.Content.ReadFromJsonAsync<UploadDetails>())!;
        var two = (await second.Content.ReadFromJsonAsync<UploadDetails>())!;

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(one.Id, two.Id);
        Assert.True(two.Replayed);
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await database.PhotoUploads.CountAsync());
        Assert.Equal(1, await database.CapturedPhotos.CountAsync());
        Assert.Equal(1, await database.PhotoContents.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusingAnIdWithChangedDataReturnsConflict(bool changeImage)
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await Login(client);
        var manifest = Manifest();
        using var first = await Send(client, manifest);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var changed = await Send(client,
            changeImage ? manifest : manifest with { NewSerialNumber = "different" },
            originalBytes: changeImage ? Jpeg : null);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("upload_id_conflict",
            (await changed.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task InvalidSecondPhotoDoesNotPartiallySaveTheBatch()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await Login(client);
        using var response = await Send(client, Manifest(2), corruptSecond: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await database.PhotoUploads.CountAsync());
        Assert.Equal(0, await database.CapturedPhotos.CountAsync());
        Assert.Equal(0, await database.PhotoContents.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    public async Task RejectsInvalidSerialNumbers(string serial)
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await Login(client);
        using var response = await Send(client, Manifest(serial: serial));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_new_serial_number",
            (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Theory]
    [InlineData(91, 23, 5)]
    [InlineData(47, -181, 5)]
    [InlineData(47, 23, -1)]
    public async Task RejectsInvalidCoordinates(double latitude, double longitude, double accuracy)
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await Login(client);
        var manifest = Manifest();
        manifest.Photos![0] = manifest.Photos[0]! with
        {
            Latitude = latitude, Longitude = longitude, AccuracyMeters = accuracy
        };
        using var response = await Send(client, manifest);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_location",
            (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task RejectsAStaleGpsMeasurement()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await Login(client);
        var manifest = Manifest();
        manifest.Photos![0] = manifest.Photos[0]! with
        {
            LocationMeasuredAtUtc = manifest.Photos[0]!.CapturedAtUtc.AddMinutes(-1)
        };
        using var response = await Send(client, manifest);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_photo_time",
            (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task RequiresBothFilesAndRejectsDuplicatePhotoIds()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await Login(client);

        using var missing = await Send(client, Manifest(), includeStamped: false);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var duplicate = Manifest(2);
        duplicate.Photos![1] = duplicate.Photos[0];
        using var repeated = await Send(client, duplicate);
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        Assert.Equal("duplicate_photo_id",
            (await repeated.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task RejectsAnOversizedFile()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await Login(client);
        using var response = await Send(client, Manifest(),
            originalBytes: new byte[8 * 1024 * 1024 + 1]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task RequiresLoginCompletedPasswordChangeAndCsrf()
    {
        using var factory = new AuthApiFactory();
        using var anonymous = factory.NewClient();
        await factory.SeedAsync();
        using var anonymousPost = await Send(anonymous, Manifest(), csrf: false);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousPost.StatusCode);

        using var pending = factory.NewClient();
        await Login(pending);
        using var firstPassword = await Send(pending, Manifest());
        Assert.Equal(HttpStatusCode.Forbidden, firstPassword.StatusCode);

        await CreateUser(factory, "ready-user");
        using var ready = factory.NewClient();
        await Login(ready, "ready-user");
        using var noCsrf = await Send(ready, Manifest(), csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        Assert.Equal("csrf_invalid", (await noCsrf.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task OtherUsersCannotReadDetailsOrEitherImageAndListsAreScoped()
    {
        using var factory = new AuthApiFactory();
        using var owner = factory.NewClient();
        using var other = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await CreateUser(factory, "other-user");
        await Login(owner);
        await Login(other, "other-user");
        using var saved = await Send(owner, Manifest());
        var upload = (await saved.Content.ReadFromJsonAsync<UploadDetails>())!;

        using var details = await other.GetAsync($"/api/uploads/{upload.Id}");
        using var original = await other.GetAsync(upload.Photos[0].OriginalUrl);
        using var stamped = await other.GetAsync(upload.Photos[0].StampedUrl);
        Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, original.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stamped.StatusCode);
        var ownerList = (await owner.GetFromJsonAsync<UploadPage>("/api/uploads"))!;
        var otherList = (await other.GetFromJsonAsync<UploadPage>("/api/uploads"))!;
        Assert.Equal(1, ownerList.Total);
        Assert.Equal(0, otherList.Total);
        using var forbidden = await other.GetAsync("/api/admin/uploads");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task AdminCanReadAllUploadsAndBothImagesWithoutLoadingBytesIntoLists()
    {
        using var factory = new AuthApiFactory();
        using var owner = factory.NewClient();
        using var admin = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await CreateUser(factory, "admin-test", admin: true);
        await Login(owner);
        await Login(admin, "admin-test");
        using var saved = await Send(owner, Manifest());
        var upload = (await saved.Content.ReadFromJsonAsync<UploadDetails>())!;

        using var list = await admin.GetAsync("/api/admin/uploads?page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var text = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("originalBytes", text);
        Assert.DoesNotContain("stampedBytes", text);
        var page = JsonSerializer.Deserialize<UploadPage>(text, JsonOptions)!;
        Assert.Equal(1, page.Total);
        Assert.Single(page.Items);
        using var details = await admin.GetAsync($"/api/uploads/{upload.Id}");
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        Assert.Equal(Png, await admin.GetByteArrayAsync(upload.Photos[0].OriginalUrl));
        Assert.Equal(Jpeg, await admin.GetByteArrayAsync(upload.Photos[0].StampedUrl));
    }

    [Fact]
    public async Task CurrentApiDoesNotAllowEditingOrDeletingSavedUploads()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await Login(client);
        using var saved = await Send(client, Manifest());
        var upload = (await saved.Content.ReadFromJsonAsync<UploadDetails>())!;

        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, $"/api/uploads/{upload.Id}");
            var token = (await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf"))!.Token;
            request.Headers.Add(AuthSetup.CsrfHeader, token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        var unchanged = (await client.GetFromJsonAsync<UploadDetails>($"/api/uploads/{upload.Id}"))!;
        Assert.Equal(upload.SerialNumber, unchanged.SerialNumber);
        Assert.Equal(upload.Version, unchanged.Version);
    }

    [Fact]
    public void ImageHeaderInspectionRejectsTruncationAndHugeDimensions()
    {
        var png = PhotoFileInspector.Inspect(Png, 40_000_000);
        var jpeg = PhotoFileInspector.Inspect(Jpeg, 40_000_000);
        Assert.Equal(new PhotoFileInfo("image/png", 16, 12), png);
        Assert.Equal(new PhotoFileInfo("image/jpeg", 16, 12), jpeg);
        Assert.Throws<UploadException>(() => PhotoFileInspector.Inspect(Png[..20], 40_000_000));
        Assert.Throws<UploadException>(() => PhotoFileInspector.Inspect(Jpeg[..20], 40_000_000));

        var oversized = (byte[])Png.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(16, 4), 10_000_000);
        Assert.Equal("invalid_photo_dimensions",
            Assert.Throws<UploadException>(() => PhotoFileInspector.Inspect(oversized, 40_000_000)).Code);
    }

    internal static UploadManifest Manifest(int count = 1, string serial = "SERIAL-123")
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        return new UploadManifest(Guid.NewGuid(),
            Enumerable.Range(0, count).Select(_ => new UploadPhotoMetadata(
                Guid.NewGuid(), 47.181912, 23.165123, 5, now, now.AddSeconds(-1))).ToArray(),
            "OLD-0001", serial);
    }

    internal static async Task Login(HttpClient client, string username = AuthApiFactory.Username)
    {
        var csrf = (await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf"))!.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Headers.Add(AuthSetup.CsrfHeader, csrf);
        request.Content = JsonContent.Create(new LoginRequest(username, AuthApiFactory.InitialPassword));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task CreateUser(AuthApiFactory factory, string username, bool admin = false)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = username, MustChangePassword = false };
        var created = await users.CreateAsync(user, AuthApiFactory.InitialPassword);
        Assert.True(created.Succeeded);
        var role = await users.AddToRoleAsync(user, admin ? AuthSetup.AdminRole : AuthSetup.UserRole);
        Assert.True(role.Succeeded);
    }

    internal static async Task<HttpResponseMessage> Send(
        HttpClient client, UploadManifest manifest, bool csrf = true,
        bool includeStamped = true, bool corruptSecond = false, byte[]? originalBytes = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/uploads");
        var form = new MultipartFormDataContent();
        request.Content = form;
        form.Add(new StringContent(JsonSerializer.Serialize(manifest, JsonOptions),
            Encoding.UTF8), "manifest");
        var index = 0;
        foreach (var photo in manifest.Photos!)
        {
            var bytes = corruptSecond && index == 1
                ? Encoding.UTF8.GetBytes("<html>not a photo</html>")
                : originalBytes ?? Png;
            var original = new ByteArrayContent(bytes);
            original.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(original, $"original_{photo!.Id:D}", "ignored-name.bin");
            if (includeStamped)
            {
                var stamped = new ByteArrayContent(Jpeg);
                stamped.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                form.Add(stamped, $"stamped_{photo.Id:D}", "stamped.jpg");
            }
            index++;
        }
        if (csrf)
        {
            var value = (await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf"))!.Token;
            request.Headers.Add(AuthSetup.CsrfHeader, value);
        }
        return await client.SendAsync(request);
    }
}