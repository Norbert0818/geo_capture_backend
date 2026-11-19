using GeoCapture.Api.Auth;
using GeoCapture.Api.Data;
using GeoCapture.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GeoCapture.Api.Tests;

public sealed class IdentityConfigurationTests
{
    [Fact]
    public void NewUsersRequirePasswordChange()
    {
        var user = new AppUser();

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.True(user.MustChangePassword);
        Assert.True(user.IsActive);
        Assert.Null(user.PasswordChangedAtUtc);
        Assert.Equal(TimeSpan.Zero, user.CreatedAtUtc.Offset);
    }

    [Theory]
    [InlineData("Abc123")]
    [InlineData("alllowercase12345")]
    [InlineData("ALLUPPERCASE12345")]
    [InlineData("LongPasswordOnly")]
    public async Task RejectsPasswordsThatDoNotMeetTheRules(string password)
    {
        using var services = CreateServices();
        using var scope = services.CreateScope();

        var manager = scope.ServiceProvider
            .GetRequiredService<UserManager<AppUser>>();

        var validator = new PasswordValidator<AppUser>();
        var result = await validator.ValidateAsync(
            manager,
            new AppUser(),
            password);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AcceptsPasswordThatMeetsTheRules()
    {
        using var services = CreateServices();
        using var scope = services.CreateScope();

        var manager = scope.ServiceProvider
            .GetRequiredService<UserManager<AppUser>>();

        var validator = new PasswordValidator<AppUser>();
        var result = await validator.ValidateAsync(
            manager,
            new AppUser(),
            "StrongPassword927");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void PasswordHashRejectsAnIncorrectPassword()
    {
        using var services = CreateServices();
        using var scope = services.CreateScope();

        var manager = scope.ServiceProvider
            .GetRequiredService<UserManager<AppUser>>();

        var user = new AppUser();
        const string password = "StrongPassword927";
        var hash = manager.PasswordHasher.HashPassword(user, password);

        Assert.NotEqual(password, hash);

        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            manager.PasswordHasher.VerifyHashedPassword(
                user,
                hash,
                password));

        Assert.Equal(
            PasswordVerificationResult.Failed,
            manager.PasswordHasher.VerifyHashedPassword(
                user,
                hash,
                "WrongPassword927"));
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();

        services.AddLogging();

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(
                "Host=localhost;Database=unused;Username=unused;Password=unused"));

        services
            .AddIdentityCore<AppUser>(IdentityConfiguration.Configure)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }
}