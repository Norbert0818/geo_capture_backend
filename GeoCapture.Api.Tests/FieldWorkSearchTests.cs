using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GeoCapture.Api.Data;
using GeoCapture.Api.FieldWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GeoCapture.Api.Tests;

public sealed class FieldWorkSearchTests
{
    private static WorkPhotoDescriptor Photo(string locality = "Sâncraiu", string county = "Cluj",
        DateTime? captured = null, int offset = 120)
    {
        var time = captured ?? new DateTime(2026, 11, 24, 22, 30, 0, DateTimeKind.Utc);
        return new(Guid.NewGuid(), "pole", 46.777987, 24.309925, 4, time, time, offset,
            locality, county, "manual", true, "image/png", "");
    }

    private static WorkDocument Document(string serial = "0002610586", string next = "0002610587",
        string kind = "replacement", WorkPhotoDescriptor[]? photos = null) =>
        new(kind, new(serial, "T0PL0138RO", "manual", null), new(next, "NEW-NTET", "manual", null),
            "A note is not a serial or a locality", photos ?? [Photo()]);

    private static async Task<Guid> Add(AuthApiFactory factory, WorkDocument document,
        bool other = false, WorkDocument? previous = null, int order = 0)
    {
        using var scope = factory.Services.CreateScope();
        var owner = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Select(x => x.Id).SingleAsync();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        var id = Guid.NewGuid();
        var now = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc).AddMinutes(order);
        var record = new FieldWorkRecord { Id = id, OwnerId = other ? Guid.NewGuid() : owner,
            OwnerName = other ? "other-worker" : AuthApiFactory.Username, CreatedAtUtc = now,
            UpdatedAtUtc = now, Revision = previous is null ? 1 : 2, Version = Guid.NewGuid() };
        db.Records.Add(record);
        void Revision(int number, WorkDocument value) => db.Revisions.Add(new FieldWorkRevision {
            Id = Guid.NewGuid(), WorkId = id, Number = number, ChangeId = Guid.NewGuid(),
            ResultVersion = Guid.NewGuid(), ActorId = owner, ActorName = record.OwnerName, AtUtc = now,
            Reason = "Test snapshot", DocumentJson = WorkRules.Serialize(value) });
        if (previous is not null) Revision(1, previous);
        Revision(record.Revision, document);
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<JsonElement> List(HttpClient client, string query = "") =>
        await client.GetFromJsonAsync<JsonElement>("/api/field-work" + query);
    private static Guid[] Ids(JsonElement response) => response.GetProperty("items").EnumerateArray()
        .Select(x => x.GetProperty("id").GetGuid()).ToArray();

    [Theory]
    [InlineData("serial", "0002610586")]
    [InlineData("serial", "0002610587")]
    [InlineData("serial", "10587")]
    [InlineData("ntet", "t0pl0138ro")]
    [InlineData("ntet", "new-ntet")]
    public async Task SearchesBothPoleIdentities(string field, string value)
    {
        using var factory = new AuthApiFactory();
        await factory.SeedAsync(mustChangePassword: false);
        var match = await Add(factory, Document());
        await Add(factory, Document("other", "different") with {
            OldPole = new("other", "unrelated", "manual", null),
            NewPole = new("different", "unrelated", "manual", null) });
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        Assert.Equal(new[] { match }, Ids(await List(client, $"?{field}={Uri.EscapeDataString(value)}")));
    }

    [Fact]
    public async Task CombinesFiltersAndReturnsBothSerialsAndPlaces()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        var match = await Add(factory, Document());
        await Add(factory, Document(kind: "repair"));
        await Add(factory, Document(photos: [Photo("Zalău", "Sălaj")]));
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        var result = await List(client, "?kind=replacement&serial=10586&ntet=138&locality=S%C3%A2ncraiu&county=CLUJ&fromDate=2026-11-25&toDate=2026-11-25");
        Assert.Equal(new[] { match }, Ids(result));
        var item = result.GetProperty("items")[0];
        Assert.Equal("0002610586", item.GetProperty("oldSerialNumber").GetString());
        Assert.Equal("0002610587", item.GetProperty("newSerialNumber").GetString());
        Assert.Equal("Sâncraiu, Cluj", item.GetProperty("locations")[0].GetString());
    }

    [Fact]
    public async Task OnlyTheCurrentRevisionIsSearched()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        var current = Document("corrected", "current", "repair", [Photo("Cluj-Napoca", "Cluj")]);
        await Add(factory, current, previous: Document("archived-old", "archived-new", photos: [Photo("Old village", "Old county")]));
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        Assert.Empty(Ids(await List(client, "?serial=archived")));
        Assert.Empty(Ids(await List(client, "?locality=Old%20village")));
        Assert.Single(Ids(await List(client, "?serial=current&kind=repair")));
    }

    [Fact]
    public async Task FilteringHappensBeforePaginationAndTheTotalIsFiltered()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        var expected = new HashSet<Guid>();
        for (var i = 0; i < 45; i++) expected.Add(await Add(factory, Document($"MATCH-{i:D3}"), order: i));
        for (var i = 0; i < 12; i++) await Add(factory, Document("NO", "NO"), order: 100 + i);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        var first = await List(client, "?serial=MATCH&page=1");
        var second = await List(client, "?serial=MATCH&page=2");
        var third = await List(client, "?serial=MATCH&page=3");
        Assert.Equal(45, first.GetProperty("total").GetInt32());
        Assert.Equal(20, Ids(first).Length); Assert.Equal(20, Ids(second).Length); Assert.Equal(5, Ids(third).Length);
        Assert.True(expected.SetEquals(Ids(first).Concat(Ids(second)).Concat(Ids(third))));
        Assert.Equal(Ids(first), Ids(await List(client, "?serial=MATCH&page=1")));
    }

    [Fact]
    public async Task PlaceAndDateMustMatchTheSameCurrentPhoto()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        await Add(factory, Document(photos: [Photo("Alpha", "Cluj"), Photo("Beta", "Sălaj",
            new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc))]));
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        Assert.Empty(Ids(await List(client, "?locality=Alpha&county=S%C4%83laj")));
        Assert.Empty(Ids(await List(client, "?locality=Beta&fromDate=2026-11-25")));
        Assert.Single(Ids(await List(client, "?locality=Alpha&county=Cluj&fromDate=2026-11-25")));
    }

    [Fact]
    public async Task CaptureDateUsesTheStoredPositiveAndNegativeOffsets()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        var positive = await Add(factory, Document(photos: [Photo()]));
        var negative = await Add(factory, Document(photos: [Photo(captured:
            new DateTime(2026, 11, 25, 1, 0, 0, DateTimeKind.Utc), offset: -300)]));
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        Assert.Equal(new[] { positive }, Ids(await List(client, "?fromDate=2026-11-25&toDate=2026-11-25")));
        Assert.Equal(new[] { negative }, Ids(await List(client, "?fromDate=2026-11-24&toDate=2026-11-24")));
    }

    [Fact]
    public async Task NormalUsersCannotSearchOtherOwners()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        var own = await Add(factory, Document()); await Add(factory, Document(), other: true);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        Assert.Equal(new[] { own }, Ids(await List(client, "?serial=10586")));
        using var all = await client.GetAsync("/api/field-work?all=true&serial=10586");
        using var owner = await client.GetAsync("/api/field-work?owner=other");
        Assert.Equal(HttpStatusCode.Forbidden, all.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, owner.StatusCode);
    }

    [Fact]
    public async Task AdminOwnerSearchRequiresTheAllUsersScope()
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(admin: true, mustChangePassword: false);
        await Add(factory, Document()); var other = await Add(factory, Document(), other: true);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        Assert.Equal(new[] { other }, Ids(await List(client, "?all=true&owner=OTHER-WORKER&serial=10587")));
        using var own = await client.GetAsync("/api/field-work?owner=other");
        Assert.Equal(HttpStatusCode.BadRequest, own.StatusCode);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("' OR 1=1 --")]
    public async Task UserTextIsLiteralAndCannotBecomeSql(string value)
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        var match = await Add(factory, Document($"prefix{value}suffix", "different"));
        await Add(factory, Document("ordinary", "normal"));
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        Assert.Equal(new[] { match }, Ids(await List(client, $"?serial={Uri.EscapeDataString(value)}")));
        var sql = WorkListSearch.Build(new(Serial: value), postgres: true);
        Assert.Contains("@f0", sql.Text); Assert.Equal(new[] { value }, sql.Values);
        if (value.Length > 1) Assert.DoesNotContain(value, sql.Text);
    }

    [Theory]
    [InlineData("kind=unknown")]
    [InlineData("kind=repair&kind=pit")]
    [InlineData("fromDate=2026-02-30")]
    [InlineData("fromDate=25-11-2026")]
    [InlineData("fromDate=2026-11-26&toDate=2026-11-25")]
    [InlineData("serial=%0A")]
    public async Task InvalidFiltersReturnAUsefulBadRequest(string query)
    {
        using var factory = new AuthApiFactory(); await factory.SeedAsync(mustChangePassword: false);
        using var client = factory.NewClient(); await UploadApiTests.Login(client);
        using var response = await client.GetAsync("/api/field-work?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("work_invalid_filters", await response.Content.ReadAsStringAsync());
    }
}