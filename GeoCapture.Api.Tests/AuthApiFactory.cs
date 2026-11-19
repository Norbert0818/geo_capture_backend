using GeoCapture.Api.Auth;
using GeoCapture.Api.Data;
using GeoCapture.Api.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GeoCapture.Api.Tests;

public sealed class AuthApiFactory : WebApplicationFactory<global::Program>
{
    public const string Username = "test-user";
    public const string InitialPassword = "InitialPassword927";
    public const string NewPassword = "ChangedPassword927";

    private readonly SqliteConnection _connection =
        new("Data Source=:memory:");

    public AuthApiFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();

            services.AddDbContext<AppDbContext>(
                options => options.UseSqlite(_connection));
        });
    }

    public HttpClient NewClient() => CreateClient(
        new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    public async Task SeedAsync(
        bool admin = false,
        bool mustChangePassword = true)
    {
        using var scope = Services.CreateScope();

        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Database.EnsureCreatedAsync();

        var roles = scope.ServiceProvider
            .GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        var users = scope.ServiceProvider
            .GetRequiredService<UserManager<AppUser>>();

        foreach (var role in new[] { AuthSetup.AdminRole, AuthSetup.UserRole })
        {
            var roleResult = await roles.CreateAsync(
                new IdentityRole<Guid>(role) { Id = Guid.NewGuid() });

            if (!roleResult.Succeeded)
            {
                throw new InvalidOperationException("Test role creation failed.");
            }
        }

        var user = new AppUser
        {
            UserName = Username,
            MustChangePassword = mustChangePassword
        };

        var result = await users.CreateAsync(user, InitialPassword);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Test user creation failed.");
        }

        result = await users.AddToRoleAsync(
            user,
            admin ? AuthSetup.AdminRole : AuthSetup.UserRole);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Test role assignment failed.");
        }
    }

    public async Task DisableUserAsync()
    {
        using var scope = Services.CreateScope();

        var users = scope.ServiceProvider
            .GetRequiredService<UserManager<AppUser>>();

        var user = (await users.FindByNameAsync(Username))!;
        user.IsActive = false;

        var result = await users.UpdateAsync(user);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Test user update failed.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _connection.Dispose();
        }
    }
}