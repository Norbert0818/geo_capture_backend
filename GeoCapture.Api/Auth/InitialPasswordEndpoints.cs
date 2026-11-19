using GeoCapture.Api.Data;
using GeoCapture.Api.Models;
using Microsoft.AspNetCore.Identity;

namespace GeoCapture.Api.Auth;

public sealed record InitialPasswordRequest(string NewPassword);

public static class InitialPasswordEndpoints
{
    public static void MapInitialPassword(this WebApplication app)
    {
        app.MapPost("/api/auth/initial-password", async Task<IResult> (
            InitialPasswordRequest request,
            HttpContext context,
            AppDbContext database,
            UserManager<AppUser> users,
            SignInManager<AppUser> signIn) =>
        {
            var user = await users.GetUserAsync(context.User);

            if (user is null || !user.IsActive)
            {
                return Results.Unauthorized();
            }

            if (!user.MustChangePassword)
            {
                return Results.Forbid();
            }

            if (string.IsNullOrEmpty(request.NewPassword) ||
                request.NewPassword.Length > 1024)
            {
                return Results.BadRequest(new ApiError("invalid_input"));
            }

            if (await users.CheckPasswordAsync(user, request.NewPassword))
            {
                return Results.BadRequest(new ApiError("password_unchanged"));
            }

            await using var transaction =
                await database.Database.BeginTransactionAsync();

            var token = await users.GeneratePasswordResetTokenAsync(user);
            var result = await users.ResetPasswordAsync(
                user,
                token,
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

            return Results.Ok(new UserResponse(
                user.Id,
                user.UserName!,
                user.MustChangePassword,
                (await users.GetRolesAsync(user)).ToArray()));
        }).RequireAuthorization(AuthSetup.SessionPolicy);
    }
}