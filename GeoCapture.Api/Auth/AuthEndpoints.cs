using GeoCapture.Api.Data;
using GeoCapture.Api.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GeoCapture.Api.Auth;

public static class AuthEndpoints
{
    public static void MapGeoCaptureAuth(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapGet("/csrf", (
            HttpContext context,
            IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new CsrfResponse(tokens.RequestToken!));
        }).AllowAnonymous();

        auth.MapPost("/login", async Task<IResult> (
            LoginRequest request,
            UserManager<AppUser> users,
            SignInManager<AppUser> signIn) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) ||
                request.Username.Length > 256 ||
                string.IsNullOrEmpty(request.Password) ||
                request.Password.Length > 1024)
            {
                return Results.BadRequest(new ApiError("invalid_input"));
            }

            var user = await users.FindByNameAsync(request.Username.Trim());

            if (user is null || !user.IsActive)
            {
                return Results.Unauthorized();
            }

            var result = await signIn.PasswordSignInAsync(
                user,
                request.Password,
                isPersistent: true,
                lockoutOnFailure: true);

            if (!result.Succeeded)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await ToResponse(user, users));
        }).AllowAnonymous().RequireRateLimiting("login");

        auth.MapGet("/me", CurrentUser)
            .RequireAuthorization(AuthSetup.SessionPolicy);

        auth.MapPost("/password", async Task<IResult> (
            ChangePasswordRequest request,
            HttpContext context,
            AppDbContext database,
            UserManager<AppUser> users,
            SignInManager<AppUser> signIn) =>
        {
            if (string.IsNullOrEmpty(request.CurrentPassword) ||
                string.IsNullOrEmpty(request.NewPassword) ||
                request.CurrentPassword.Length > 1024 ||
                request.NewPassword.Length > 1024)
            {
                return Results.BadRequest(new ApiError("invalid_input"));
            }

            if (request.CurrentPassword == request.NewPassword)
            {
                return Results.BadRequest(new ApiError("password_unchanged"));
            }

            var user = await users.GetUserAsync(context.User);

            if (user is null || !user.IsActive)
            {
                return Results.Unauthorized();
            }

            await using var transaction =
                await database.Database.BeginTransactionAsync();

            var result = await users.ChangePasswordAsync(
                user,
                request.CurrentPassword,
                request.NewPassword);

            if (!result.Succeeded)
            {
                return Results.BadRequest(new ApiError(
                    "password_change_failed",
                    result.Errors.Select(x => x.Code).ToArray()));
            }

            user.MustChangePassword = false;
            user.PasswordChangedAtUtc = DateTimeOffset.UtcNow;

            result = await users.UpdateAsync(user);

            if (!result.Succeeded)
            {
                return Results.Conflict(new ApiError("update_conflict"));
            }

            await transaction.CommitAsync();
            await signIn.RefreshSignInAsync(user);

            return Results.Ok(await ToResponse(user, users));
        }).RequireAuthorization(AuthSetup.SessionPolicy);

        auth.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Results.NoContent();
        }).RequireAuthorization(AuthSetup.SessionPolicy);

        app.MapGet("/api/account", CurrentUser)
            .RequireAuthorization();

        app.MapGet("/api/admin/users", async (AppDbContext database) =>
        {
            return Results.Ok(await database.Users
                .AsNoTracking()
                .OrderBy(x => x.UserName)
                .Select(x => new
                {
                    x.Id,
                    x.UserName,
                    x.IsActive,
                    x.MustChangePassword
                })
                .Take(100)
                .ToListAsync());
        }).RequireAuthorization(AuthSetup.AdminPolicy);
    }

    private static async Task<IResult> CurrentUser(
        HttpContext context,
        UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(context.User);

        return user is null
            ? Results.Unauthorized()
            : Results.Ok(await ToResponse(user, users));
    }

    private static async Task<UserResponse> ToResponse(
        AppUser user,
        UserManager<AppUser> users)
    {
        return new UserResponse(
            user.Id,
            user.UserName!,
            user.MustChangePassword,
            (await users.GetRolesAsync(user)).ToArray());
    }
}