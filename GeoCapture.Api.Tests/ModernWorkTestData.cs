using System.Net.Http.Json;
using System.Text;
using GeoCapture.Api.Auth;
using GeoCapture.Api.Data;
using GeoCapture.Api.FieldWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualBasic.FileIO;

namespace GeoCapture.Api.Tests;

internal static class ModernWorkTestData
{
    internal static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABAAAAAMCAIAAADkharWAAAAF0lEQVR4nGN0y5vGQApgIkn1qIYRpAEAER0BYmrANbUAAAAASUVORK5CYII=");
    internal static readonly byte[] Jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAAMABADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDGooor2Dyz/9k=");

    internal static WorkPhotoDescriptor Photo(string role = "pole", string localityCode = "59443",
        string locality = "Sâncraiu", string countyCode = "12", string county = "Cluj", int minutes = 0)
    {
        var at = DateTime.UtcNow.AddMinutes(-5 + minutes);
        var value = new WorkPhotoDescriptor(Guid.NewGuid(), role, 46.777987, 24.309925, 3.2,
            at, at.AddSeconds(-1), 120, locality, county, "catalog", false, "image/png", "", countyCode, localityCode);
        return value with { OverlayText = WorkRules.Overlay(value) };
    }
    internal static PoleIdentity Pole(string serial = "0002610586", string state = "present") =>
        new(serial, "", "manual", null, state, true);
    internal static WorkDocument Document(string kind = "replacement", string progress = "completed") =>
        new(kind, kind == "pit" ? null : Pole(), kind == "replacement" ? Pole("0002610587") : null, "",
            ModernWorkRules.OverviewRoles(kind).Select(role => Photo(role)).ToArray(), 2, progress);

    internal static async Task<HttpResponseMessage> Send(HttpClient client, Guid id, WorkSaveRequest input,
        IEnumerable<WorkPhotoDescriptor>? attach = null)
    {
        var csrf = (await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf"))!;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/field-work/{id}/revisions");
        request.Headers.Add(AuthSetup.CsrfHeader, csrf.Token);
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(WorkRules.Serialize(input)), "manifest");
        foreach (var photo in attach ?? input.Document.Photos)
        {
            content.Add(new ByteArrayContent(Png), $"original_{photo.Id:D}", "original.png");
            content.Add(new ByteArrayContent(Jpeg), $"stamped_{photo.Id:D}", "stamped.jpg");
        }
        request.Content = content;
        return await client.SendAsync(request);
    }
    internal static async Task<Guid> Seed(AuthApiFactory factory, WorkDocument document, bool other = false,
        int count = 1, WorkDocument? previous = null)
    {
        using var scope = factory.Services.CreateScope();
        var owner = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Select(x => x.Id).SingleAsync();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        var first = Guid.Empty;
        for (var index = 0; index < count; index++)
        {
            var id = Guid.NewGuid(); if (index == 0) first = id;
            var time = DateTime.UtcNow.AddSeconds(index);
            var record = new FieldWorkRecord { Id = id, OwnerId = other ? Guid.NewGuid() : owner,
                OwnerName = other ? "another-worker" : AuthApiFactory.Username,
                CreatedAtUtc = time, UpdatedAtUtc = time, Revision = previous is null ? 1 : 2, Version = Guid.NewGuid() };
            db.Records.Add(record);
            void Add(WorkDocument doc, int number) => db.Revisions.Add(new FieldWorkRevision {
                Id = Guid.NewGuid(), WorkId = id, Number = number, ChangeId = Guid.NewGuid(),
                ResultVersion = number == record.Revision ? record.Version : Guid.NewGuid(),
                ActorId = owner, ActorName = record.OwnerName, AtUtc = time, Reason = "Test revision",
                DocumentJson = WorkRules.Serialize(doc) });
            if (previous is not null) Add(previous, 1);
            Add(document, record.Revision);
        }
        await db.SaveChangesAsync(); return first;
    }
    internal static List<string[]> Csv(byte[] bytes)
    {
        using var parser = new TextFieldParser(new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')))
            { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var result = new List<string[]>();
        while (!parser.EndOfData) result.Add(parser.ReadFields()!);
        return result;
    }
    internal static string Cell(List<string[]> rows, int row, string header) => rows[row][Array.IndexOf(rows[0], header)];
}