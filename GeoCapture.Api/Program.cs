using System.Threading.RateLimiting;
using GeoCapture.Api.Auth;
using GeoCapture.Api.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var geoWorkingDirectory = Directory.GetCurrentDirectory();

var geoProjectRoot = new[]
{
    geoWorkingDirectory,
    Path.Combine(geoWorkingDirectory, "GeoCapture.Api")
}.FirstOrDefault(directory =>
    File.Exists(Path.Combine(directory, "GeoCapture.Api.csproj")));

var geoBinaryRoot =
    Path.GetDirectoryName(typeof(AppDbContext).Assembly.Location);

if (string.IsNullOrWhiteSpace(geoBinaryRoot))
{
    geoBinaryRoot = AppContext.BaseDirectory;
}

var geoEnvCandidates = new[]
{
    geoProjectRoot is null ? null : Path.Combine(geoProjectRoot, ".env"),
    Path.Combine(geoBinaryRoot, ".env"),
    Path.Combine(AppContext.BaseDirectory, ".env")
};

var geoEnvPath = geoEnvCandidates.FirstOrDefault(path =>
    path is not null && File.Exists(path));

if (geoEnvPath is not null)
{
    try
    {
        DotNetEnv.Env.NoClobber().Load(geoEnvPath);
    }
    catch
    {
        throw new InvalidOperationException(
            $"The .env file could not be loaded. Check its format: {geoEnvPath}");
    }
}

var geoConfigurationRoot = geoEnvPath is null
    ? geoProjectRoot ?? geoBinaryRoot
    : Path.GetDirectoryName(geoEnvPath)!;

var bootstrap = args.FirstOrDefault() == "bootstrap-admin";

var builder = WebApplication.CreateBuilder(
    bootstrap ? Array.Empty<string>() : args);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString =
        builder.Configuration.GetConnectionString("Postgres");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "ConnectionStrings:Postgres is missing. Check the API .env file.");
    }

    var settings = new NpgsqlConnectionStringBuilder(connectionString);
    var certificate = settings.RootCertificate;

    if (!string.IsNullOrWhiteSpace(certificate))
    {
        var configuredPath = Path.GetFullPath(
            certificate, geoConfigurationRoot);
        var outputPath = Path.GetFullPath(
            certificate, geoBinaryRoot);

        var certificatePath = File.Exists(configuredPath)
            ? configuredPath
            : outputPath;

        if (!File.Exists(certificatePath))
        {
            throw new FileNotFoundException(
                "The PostgreSQL CA certificate was not found.",
                certificatePath);
        }

        settings.RootCertificate = certificatePath;
    }

    options.UseNpgsql(settings.ConnectionString, postgres =>
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

if (!app.Environment.IsEnvironment("Testing"))
{
    Console.WriteLine(
        $"GeoCapture .env file: {geoEnvPath ?? "(not found)"}");

    if (string.IsNullOrWhiteSpace(
        app.Configuration.GetConnectionString("Postgres")))
    {
        var message = geoEnvPath is null
            ? "No PostgreSQL connection string is configured. " +
              "Place .env next to GeoCapture.Api.csproj."
            : "The loaded .env file does not provide a non-empty " +
              "ConnectionStrings__Postgres value.";

        throw new InvalidOperationException(message);
    }

    // Validate configuration and certificate paths before accepting requests.
    // Resolving the context does not open a database connection.
    using var configurationScope = app.Services.CreateScope();
    _ = configurationScope.ServiceProvider
        .GetRequiredService<AppDbContext>();
}

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
