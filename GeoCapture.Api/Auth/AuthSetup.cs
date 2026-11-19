using System.Security.Claims;
using GeoCapture.Api.Data;
using GeoCapture.Api.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace GeoCapture.Api.Auth;

public static class AuthSetup
{
    public const string SessionPolicy = "Session";
    public const string AdminPolicy = "Admin";
    public const string AdminRole = "Admin";
    public const string UserRole = "User";
    public const string ReadyClaim = "password_ready";
    public const string CsrfHeader = "X-CSRF-Token";

    public static IServiceCollection AddGeoCaptureAuth(
        this IServiceCollection services)
    {
        services
            .AddIdentityCore<AppUser>(IdentityConfiguration.Configure)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services
            .AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, options =>
            {
                options.Cookie.Name = "__Host-geocapture";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;

                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };

                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };

                options.Events.OnValidatePrincipal = async context =>
                {
                    var users = context.HttpContext.RequestServices
                        .GetRequiredService<UserManager<AppUser>>();

                    var user = await users.GetUserAsync(context.Principal!);
                    var stamp = context.Principal!.FindFirstValue(
                        users.Options.ClaimsIdentity.SecurityStampClaimType);

                    if (user is null || !user.IsActive ||
                        string.IsNullOrEmpty(stamp) ||
                        stamp != user.SecurityStamp)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(
                            IdentityConstants.ApplicationScheme);
                        return;
                    }

                    var signIn = context.HttpContext.RequestServices
                        .GetRequiredService<SignInManager<AppUser>>();

                    var principal = await signIn.CreateUserPrincipalAsync(user);
                    var identity = (ClaimsIdentity)principal.Identity!;

                    identity.AddClaim(new Claim(
                        ReadyClaim,
                        user.MustChangePassword ? "false" : "true"));

                    context.ReplacePrincipal(principal);
                };
            });

        services.AddAntiforgery(options =>
        {
            options.HeaderName = CsrfHeader;
            options.Cookie.Name = "__Host-geocapture.csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });

        services.AddAuthorization(options =>
        {
            var session = new AuthorizationPolicyBuilder(
                IdentityConstants.ApplicationScheme)
                .RequireAuthenticatedUser()
                .Build();

            var ready = new AuthorizationPolicyBuilder(session)
                .RequireClaim(ReadyClaim, "true")
                .Build();

            options.AddPolicy(SessionPolicy, session);
            options.DefaultPolicy = ready;
            options.FallbackPolicy = ready;

            options.AddPolicy(
                AdminPolicy,
                new AuthorizationPolicyBuilder(ready)
                    .RequireRole(AdminRole)
                    .Build());
        });

        return services;
    }
}