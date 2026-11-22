using GeoCapture.Api.Auth;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GeoCapture.Api.Uploads;

public static class UploadSetup
{
    public static void AddGeoCaptureUploads(this IServiceCollection services)
    {
        services.AddOptions<UploadOptions>()
            .BindConfiguration("Uploads")
            .Validate(x => x.MaxPhotos is >= 1 and <= 50 &&
                x.MaxSerialNumberLength is >= 1 and <= 1024 &&
                x.MaxFileBytes > 0 && x.MaxFileBytes <= 32L * 1024 * 1024 &&
                x.MaxBatchBytes >= x.MaxFileBytes &&
                x.MaxRequestBytes > x.MaxBatchBytes &&
                x.MaxRequestBytes <= 256L * 1024 * 1024 &&
                x.MaxImagePixels is >= 1 and <= 100_000_000,
                "Invalid Uploads configuration.")
            .ValidateOnStart();

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<UploadReader>();
    }

    public static void UseGeoCaptureUploadLimits(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method) &&
                context.Request.Path.StartsWithSegments("/api/uploads"))
            {
                var limit = context.RequestServices
                    .GetRequiredService<IOptions<UploadOptions>>().Value.MaxRequestBytes;

                if (context.Request.ContentLength is long length && length > limit)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await context.Response.WriteAsJsonAsync(new ApiError("upload_too_large"));
                    return;
                }

                var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false })
                {
                    feature.MaxRequestBodySize = limit;
                }
            }

            await next(context);
        });
    }
}