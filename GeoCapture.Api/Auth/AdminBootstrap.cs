using System.Security.Cryptography;
using GeoCapture.Api.Data;
using GeoCapture.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace GeoCapture.Api.Auth;

public static class AdminBootstrap
{
    public static async Task RunAsync(
        IServiceProvider services,
        string username)
    {
        await using var scope = services.CreateAsyncScope();

        var database = scope.ServiceProvider
            .GetRequiredService<AppDbContext>();

        var users = scope.ServiceProvider
            .GetRequiredService<UserManager<AppUser>>();

        var roles = scope.ServiceProvider
            .GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        if ((await database.Database.GetPendingMigrationsAsync()).Any())
        {
            throw new InvalidOperationException("Apply database migrations first.");
        }

        await using var transaction =
            await database.Database.BeginTransactionAsync();

        foreach (var name in new[] { AuthSetup.AdminRole, AuthSetup.UserRole })
        {
            if (!await roles.RoleExistsAsync(name))
            {
                EnsureSuccess(await roles.CreateAsync(
                    new IdentityRole<Guid>(name) { Id = Guid.NewGuid() }));
            }
        }

        if ((await users.GetUsersInRoleAsync(AuthSetup.AdminRole)).Count > 0)
        {
            throw new InvalidOperationException("An administrator already exists.");
        }

        var password = "Gc9!" +
            WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(18));

        var user = new AppUser { UserName = username.Trim() };

        EnsureSuccess(await users.CreateAsync(user, password));
        EnsureSuccess(await users.AddToRoleAsync(user, AuthSetup.AdminRole));

        await transaction.CommitAsync();

        Console.WriteLine("Administrator created.");
        Console.WriteLine("Username: " + user.UserName);
        Console.WriteLine("Temporary password: " + password);
        Console.WriteLine("Change this password on the first sign-in.");
    }

    private static void EnsureSuccess(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                string.Join(", ", result.Errors.Select(x => x.Code)));
        }
    }
}