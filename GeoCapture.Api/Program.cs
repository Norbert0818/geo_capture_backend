using System.Threading.RateLimiting;
using GeoCapture.Api.Auth;
using GeoCapture.Api.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var geoEnvPath = Path.Combine(AppContext.BaseDirectory, ".env");

if (File.Exists(geoEnvPath))
{
    try
    {
        DotNetEnv.Env.NoClobber().Load(geoEnvPath);
    }
    catch
    {
        throw new InvalidOperationException(
            "The API .env file could not be loaded. Check its format.");
    }
}

var bootstrap = args.FirstOrDefault() == "bootstrap-admin";

var builder = WebApplication.CreateBuilder(
    bootstrap ? Array.Empty<string>() : args);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Postgres");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "Configure ConnectionStrings:Postgres before starting the API.");
    }

    options.UseNpgsql(connectionString, postgres =>
        postgres.MigrationsHistoryTable(
            "__EFMigrationsHistory",
            AppDbContext.Schema));
});

builder.Services.AddGeoCaptureAuth();
builder.Services.AddProblemDetails();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var app = builder.Build();

if (bootstrap)
{
    if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
    {
        throw new ArgumentException("Usage: bootstrap-admin USERNAME");
    }

    await AdminBootstrap.RunAsync(app.Services, args[1]);
    return;
}

app.UseExceptionHandler();
app.UseHttpsRedirection();

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".frag"] = "application/octet-stream";
contentTypes.Mappings[".bin"] = "application/octet-stream";
contentTypes.Mappings[".wasm"] = "application/wasm";

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }

    if (!HttpMethods.IsGet(context.Request.Method) &&
        !HttpMethods.IsHead(context.Request.Method) &&
        !HttpMethods.IsOptions(context.Request.Method))
    {
        var antiforgery = context.RequestServices
            .GetRequiredService<IAntiforgery>();

        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ApiError("csrf_invalid"));
            return;
        }
    }

    await next(context);
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous();

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
        }).AllowAnonymous();
}

app.MapGeoCaptureAuth();
app.MapInitialPassword();
app.Run();

public partial class Program { }