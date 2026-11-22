using System.Security.Claims;
using System.Security.Cryptography;
using GeoCapture.Api.Auth;
using GeoCapture.Api.Data;
using GeoCapture.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace GeoCapture.Api.Admin;

public static class AdminAccountEndpoints
{
    public static void MapAdminAccounts(this WebApplication app)
    {
        var accounts = app.MapGroup("/api/admin/accounts")
            .RequireAuthorization(AuthSetup.AdminPolicy);
        accounts.MapGet("", List);
        accounts.MapPost("", Create);
        accounts.MapPost("/{id:guid}/status", SetStatus);
        accounts.MapPost("/{id:guid}/temporary-password", NewTemporaryPassword);
    }

    private static async Task<IResult> List(
        int? page, int? pageSize, AppDbContext database, CancellationToken token)
    {
        var number = page ?? 1;
        var size = pageSize ?? 12;
        if (number < 1 || size is < 1 or > 50 || (long)(number - 1) * size > int.MaxValue)
            return Results.BadRequest(new ApiError("invalid_page"));

        var total = await database.Users.CountAsync(token);
        var rows = await database.Users.AsNoTracking()
            .OrderBy(user => user.NormalizedUserName).ThenBy(user => user.Id)
            .Skip((number - 1) * size).Take(size).ToArrayAsync(token);
        var ids = rows.Select(user => user.Id).ToArray();
        var memberships = await (
            from membership in database.UserRoles
            join role in database.Roles on membership.RoleId equals role.Id
            where ids.Contains(membership.UserId)
            select new { membership.UserId, role.Name }).ToArrayAsync(token);
        var roles = memberships.ToLookup(item => item.UserId, item => item.Name!);
        return Results.Ok(new AdminAccountPage(number, size, total,
            rows.Select(user => Response(user, roles[user.Id])).ToArray()));
    }

    private static async Task<IResult> Create(
        CreateAccountRequest request, AppDbContext database,
        UserManager<AppUser> users, RoleManager<IdentityRole<Guid>> roles)
    {
        var username = request.Username?.Trim();
        if (string.IsNullOrEmpty(username) || username.Length > 64)
            return Results.BadRequest(new ApiError("invalid_username"));
        if (await users.FindByNameAsync(username) is not null)
            return Results.Conflict(new ApiError("username_exists"));
        if (!await roles.RoleExistsAsync(AuthSetup.UserRole))
            return Results.Problem(statusCode: 503, title: "The user role is unavailable.");

        var password = Password();
        var user = new AppUser { UserName = username, IsActive = true, MustChangePassword = true };
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync();
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
                return Results.Json(new ApiError(created.Errors.Any(e => e.Code == "DuplicateUserName")
                    ? "username_exists" : "invalid_username"),
                    statusCode: created.Errors.Any(e => e.Code == "DuplicateUserName") ? 409 : 400);

            var assigned = await users.AddToRoleAsync(user, AuthSetup.UserRole);
            if (!assigned.Succeeded)
                return Results.Problem(statusCode: 503, title: "The user role could not be assigned.");

            await transaction.CommitAsync();
            // Only the password hash is stored. Plaintext is returned for this response.
            return Results.Json(
                new AccountCredentials(Response(user, [AuthSetup.UserRole]), password), statusCode: 201);
        }
        catch (DbUpdateException)
        {
            // A concurrent request may have taken the unique normalized username.
            database.ChangeTracker.Clear();
            if (await users.FindByNameAsync(username) is not null)
                return Results.Conflict(new ApiError("username_exists"));
            throw;
        }
    }

    private static async Task<IResult> SetStatus(
        Guid id, AccountStatusRequest request, HttpContext context, UserManager<AppUser> users)
    {
        if (request.IsActive is null || string.IsNullOrEmpty(request.Version))
            return Results.BadRequest(new ApiError("invalid_input"));
        var (user, failure) = await Editable(id, request.Version, context, users);
        if (failure is not null) return failure;
        if (user!.IsActive != request.IsActive.Value)
        {
            user.IsActive = request.IsActive.Value;
            // UpdateSecurityStampAsync saves the new status and stamp together.
            // Re-enabling an account must not revive any earlier session cookie.
            var result = await users.UpdateSecurityStampAsync(user);
            if (!result.Succeeded) return Results.Conflict(new ApiError("account_conflict"));
        }
        return Results.Ok(Response(user, await users.GetRolesAsync(user)));
    }

    private static async Task<IResult> NewTemporaryPassword(
        Guid id, TemporaryPasswordRequest request, HttpContext context, UserManager<AppUser> users)
    {
        if (string.IsNullOrEmpty(request.Version))
            return Results.BadRequest(new ApiError("invalid_input"));
        var (user, failure) = await Editable(id, request.Version, context, users);
        if (failure is not null) return failure;
        if (!user!.MustChangePassword)
            return Results.Conflict(new ApiError("initial_password_completed"));

        var password = Password();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var reset = await users.ResetPasswordAsync(user, token, password);
        if (!reset.Succeeded) return Results.Conflict(new ApiError("account_conflict"));
        return Results.Ok(new AccountCredentials(
            Response(user, await users.GetRolesAsync(user)), password));
    }

    private static async Task<(AppUser? User, IResult? Failure)> Editable(
        Guid id, string version, HttpContext context, UserManager<AppUser> users)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
            return (null, Results.Unauthorized());
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return (null, Results.NotFound());
        if (user.Id == actor || await users.IsInRoleAsync(user, AuthSetup.AdminRole))
            return (null, Results.Conflict(new ApiError("account_protected")));
        if (!string.Equals(user.ConcurrencyStamp, version, StringComparison.Ordinal))
            return (null, Results.Conflict(new ApiError("account_conflict")));
        return (user, null);
    }

    private static string Password() =>
        "Gc9!" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));

    private static AdminAccountResponse Response(AppUser user, IEnumerable<string> roles) => new(
        user.Id, user.UserName!, user.IsActive, user.MustChangePassword,
        user.CreatedAtUtc, user.ConcurrencyStamp!, roles.OrderBy(role => role).ToArray());
}