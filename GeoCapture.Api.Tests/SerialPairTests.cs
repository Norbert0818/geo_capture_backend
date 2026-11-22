using System.Net;
using System.Net.Http.Json;
using GeoCapture.Api.Data;
using GeoCapture.Api.Models;
using GeoCapture.Api.Uploads;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GeoCapture.Api.Tests;

public sealed class SerialPairTests
{
    [Fact]
    public async Task BothSerialsAreTrimmedStoredAndReturnedByListAndDetails()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await UploadApiTests.Login(client);
        var manifest = UploadApiTests.Manifest() with
        {
            OldSerialNumber = "  000042 / OLD  ",
            NewSerialNumber = "  000073 / NEW  "
        };
        using var response = await UploadApiTests.Send(client, manifest);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = (await response.Content.ReadFromJsonAsync<UploadDetails>())!;
        Assert.Equal("000042 / OLD", receipt.OldSerialNumber);
        Assert.Equal("000073 / NEW", receipt.NewSerialNumber);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await database.PhotoUploads.SingleAsync();
        Assert.Equal(receipt.OldSerialNumber, saved.OldSerialNumber);
        Assert.Equal(receipt.NewSerialNumber, saved.SerialNumber);
        var page = (await client.GetFromJsonAsync<UploadPage>("/api/uploads"))!;
        Assert.Equal(receipt.OldSerialNumber, page.Items.Single().OldSerialNumber);
        Assert.Equal(receipt.NewSerialNumber, page.Items.Single().NewSerialNumber);
        var detail = (await client.GetFromJsonAsync<UploadDetails>($"/api/uploads/{receipt.Id}"))!;
        Assert.Equal(receipt.OldSerialNumber, detail.OldSerialNumber);
        Assert.Equal(receipt.NewSerialNumber, detail.NewSerialNumber);
    }

    [Theory]
    [InlineData(true, null, "invalid_old_serial_number")]
    [InlineData(true, " ", "invalid_old_serial_number")]
    [InlineData(true, "old\nserial", "invalid_old_serial_number")]
    [InlineData(false, null, "invalid_new_serial_number")]
    [InlineData(false, " ", "invalid_new_serial_number")]
    [InlineData(false, "new\nserial", "invalid_new_serial_number")]
    public async Task RequiresBothSerialsAndRejectsControlCharacters(bool old, string? value, string code)
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await UploadApiTests.Login(client);
        var manifest = UploadApiTests.Manifest();
        manifest = old ? manifest with { OldSerialNumber = value } : manifest with { NewSerialNumber = value };
        using var response = await UploadApiTests.Send(client, manifest);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<GeoCapture.Api.Auth.ApiError>();
        Assert.Equal(code, error!.Code);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().PhotoUploads.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EachSerialHasItsOwnLengthLimit(bool old)
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await UploadApiTests.Login(client);
        var manifest = UploadApiTests.Manifest();
        manifest = old
            ? manifest with { OldSerialNumber = new string('A', 257) }
            : manifest with { NewSerialNumber = new string('A', 257) };
        using var response = await UploadApiTests.Send(client, manifest);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangingOnlyTheOldSerialOnRetryIsAConflict()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await UploadApiTests.Login(client);
        var manifest = UploadApiTests.Manifest();
        using var first = await UploadApiTests.Send(client, manifest);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var changed = await UploadApiTests.Send(client, manifest with { OldSerialNumber = "OTHER-OLD" });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
    }

    [Fact]
    public async Task LegacyUploadKeepsItsOriginalValueWithoutInventingASerialPair()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await UploadApiTests.Login(client);
        using var saved = await UploadApiTests.Send(client, UploadApiTests.Manifest(serial: "LEGACY-0001"));
        var receipt = (await saved.Content.ReadFromJsonAsync<UploadDetails>())!;
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await database.PhotoUploads.SingleAsync();
            row.OldSerialNumber = null;
            await database.SaveChangesAsync();
        }
        var detail = (await client.GetFromJsonAsync<UploadDetails>($"/api/uploads/{receipt.Id}"))!;
        Assert.Equal("LEGACY-0001", detail.SerialNumber);
        Assert.Null(detail.OldSerialNumber);
        Assert.Null(detail.NewSerialNumber);
        Assert.NotEmpty(await client.GetByteArrayAsync(detail.Photos.Single().OriginalUrl));
        var page = (await client.GetFromJsonAsync<UploadPage>("/api/uploads"))!;
        Assert.Equal("LEGACY-0001", page.Items.Single().SerialNumber);
        Assert.Null(page.Items.Single().NewSerialNumber);
    }

    [Fact]
    public void PostgreSqlSchemaChangeOnlyAddsOneNullableColumn()
    {
        const string connection = "Host=localhost;Database=schema_check;Username=schema_check;Password=unused";
        using var legacy = new LegacySchemaContext(new DbContextOptionsBuilder<LegacySchemaContext>()
            .UseNpgsql(connection).Options);
        using var current = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection).Options);
        var source = legacy.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var target = current.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var operations = current.GetService<IMigrationsModelDiffer>().GetDifferences(source, target);
        var added = Assert.IsType<AddColumnOperation>(Assert.Single(operations));
        Assert.Equal("OldSerialNumber", added.Name);
        Assert.Equal("PhotoUploads", added.Table);
        Assert.True(added.IsNullable);
        Assert.Equal(1024, added.MaxLength);
    }

    private sealed class LegacySchemaContext(DbContextOptions<LegacySchemaContext> options)
        : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.HasDefaultSchema(AppDbContext.Schema);
            builder.Entity<AppUser>().Property(x => x.MustChangePassword).HasDefaultValue(true);
            builder.Entity<AppUser>().Property(x => x.IsActive).HasDefaultValue(true);
            builder.ConfigurePhotoUploads();
            builder.Entity<PhotoUpload>().Ignore(x => x.OldSerialNumber);
        }
    }
}