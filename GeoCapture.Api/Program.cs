using GeoCapture.Api.Auth;
using GeoCapture.Api.Data;
using GeoCapture.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Configure ConnectionStrings:Postgres before starting the API.");
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString, postgres =>
        postgres.MigrationsHistoryTable(
            "__EFMigrationsHistory",
            AppDbContext.Schema)));

builder.Services
    .AddIdentityCore<AppUser>(IdentityConfiguration.Configure)
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseHttpsRedirection();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

if (app.Environment.IsDevelopment())
{
    app.MapGet(
        "/health/database",
        async Task<IResult> (AppDbContext database, CancellationToken token) =>
        {
            try
            {
                await database.Users.AnyAsync(token);

                return Results.Ok(new { status = "ok" });
            }
            catch (NpgsqlException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Database is not ready.");
            }
        });
}

app.Run();