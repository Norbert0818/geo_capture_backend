using System.Net;
using System.Net.Http.Json;
using GeoCapture.Api.Admin;
using GeoCapture.Api.Auth;
using GeoCapture.Api.Data;
using GeoCapture.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GeoCapture.Api.Tests;

public sealed class AdminAccountsTests
{
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("user", HttpStatusCode.Forbidden)]
    [InlineData("pending-admin", HttpStatusCode.Forbidden)]
    public async Task OnlyReadyAdministratorsCanManageAccounts(string actor, HttpStatusCode expected)
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(admin: actor != "user", mustChangePassword: actor == "pending-admin");
        if (actor != "anonymous") await Login(client);
        using var list = await client.GetAsync("/api/admin/accounts");
        using var create = await Post(client, "/api/admin/accounts", new { username = "worker01" });
        Assert.Equal(expected, list.StatusCode);
        Assert.Equal(expected, create.StatusCode);
    }

    [Fact]
    public async Task CreatesOnlyAUserWithHashedGeneratedPasswordAndRequiredFirstChange()
    {
        using var factory = new AuthApiFactory();
        using var admin = factory.NewClient();
        await factory.SeedAsync(admin: true, mustChangePassword: false);
        await Login(admin);
        using var response = await Post(admin, "/api/admin/accounts", new { username = "  worker01  ", roles = new[] { "Admin" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var created = (await response.Content.ReadFromJsonAsync<AccountCredentials>())!;
        Assert.Equal("worker01", created.Account.Username);
        Assert.True(created.Account.IsActive);
        Assert.True(created.Account.MustChangePassword);
        Assert.Equal(new[] { "User" }, created.Account.Roles);
        Assert.True(created.TemporaryPassword.Length >= 28);
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var stored = (await users.FindByNameAsync("worker01"))!;
        Assert.NotEqual(created.TemporaryPassword, stored.PasswordHash);
        Assert.True(await users.CheckPasswordAsync(stored, created.TemporaryPassword));
        Assert.Equal(stored.ConcurrencyStamp, created.Account.Version);
    }

    [Fact]
    public async Task NewUserChangesOnlyTheInitialPasswordAndCannotAccessAdministration()
    {
        using var factory = new AuthApiFactory();
        using var admin = factory.NewClient();
        using var worker = factory.NewClient();
        await factory.SeedAsync(admin: true, mustChangePassword: false);
        await Login(admin);
        var credentials = await Create(admin);
        await Login(worker, credentials.Account.Username, credentials.TemporaryPassword);
        using var blocked = await worker.GetAsync("/api/uploads");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        using var changed = await Post(worker, "/api/auth/initial-password", new { newPassword = AuthApiFactory.NewPassword });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var uploads = await worker.GetAsync("/api/uploads");
        using var forbidden = await worker.GetAsync("/api/admin/accounts");
        Assert.Equal(HttpStatusCode.OK, uploads.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var logout = await Post(worker, "/api/auth/logout", new { });
        await Login(worker, credentials.Account.Username, AuthApiFactory.NewPassword);
        Assert.False((await worker.GetFromJsonAsync<UserResponse>("/api/auth/me"))!.MustChangePassword);
    }

    [Fact]
    public async Task DuplicateNormalizedUsernameDoesNotCreateAnotherAccountOrRevealAPassword()
    {
        using var factory = new AuthApiFactory();
        using var admin = factory.NewClient();
        await factory.SeedAsync(admin: true, mustChangePassword: false);
        await Login(admin);
        await Create(admin);
        using var duplicate = await Post(admin, "/api/admin/accounts", new { username = " WORKER01 " });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("username_exists", (await duplicate.Content.ReadFromJsonAsync<ApiError>())!.Code);
        Assert.DoesNotContain("temporaryPassword", await duplicate.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad name")]
    public async Task InvalidUsernamesAreRejectedWithoutSaving(string username)
    {
        using var factory = new AuthApiFactory();
        using var admin = factory.NewClient();
        await factory.SeedAsync(admin: true, mustChangePassword: false);
        await Login(admin);
        using var response = await Post(admin, "/api/admin/accounts", new { username });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync());
    }

    [Fact]
    public async Task MissingCsrfPreventsAccountCreation()
    {
        using var factory = new AuthApiFactory();
        using var admin = factory.NewClient();
        await factory.SeedAsync(admin: true, mustChangePassword: false);
        await Login(admin);
        using var response = await admin.PostAsJsonAsync("/api/admin/accounts", new { username = "worker01" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("csrf_invalid", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task DisablingAndReEnablingCannotReviveEarlierCookiesOrRepeatTheFirstChange()
    {
        using var factory = new AuthApiFactory();
        using var admin = factory.NewClient();
        using var oldOne = factory.NewClient();
        using var oldTwo = factory.NewClient();
        using var fresh = factory.NewClient();
        await factory.SeedAsync(admin: true, mustChangePassword: false);
        await Login(admin);
        var row = await AddUser(factory, "ready-worker", pending: false);
        await Login(oldOne, row.Username);
        await Login(oldTwo, row.Username);
        using var disabled = await Post(admin, $"/api/admin/accounts/{row.Id}/status", new { isActive = false, version = row.Version });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var inactive = (await disabled.Content.ReadFromJsonAsync<AdminAccountResponse>())!;
        Assert.False(inactive.IsActive);
        Assert.False(inactive.MustChangePassword);
        using var denied = await oldOne.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var enable = await Post(admin, $"/api/admin/accounts/{row.Id}/status", new { isActive = true, version = inactive.Version });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        using var oldCookie = await oldTwo.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, oldCookie.StatusCode);
        await Login(fresh, row.Username);
        Assert.False((await fresh.GetFromJsonAsync<UserResponse>("/api/auth/me"))!.MustChangePassword);
    }

    [Fact]
    public async Task AdministratorAccountsIncludingSelfAreProtected()
    {
        using var factory = new AuthApiFactory();
        using var admin = factory.NewClient();
        await factory.SeedAsync(admin: true, mustChangePassword: false);
        await Login(admin);
        var other = await AddUser(factory, "another-admin", admin: true);
        var page = (await admin.GetFromJsonAsync<AdminAccountPage>("/api/admin/accounts"))!;
        foreach (var account in page.Items)
        {
            using var response = await Post(admin, $"/api/admin/accounts/{account.Id}/status", new { isActive = false, version = account.Version });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("account_protected", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
        }
        using var password = await Post(admin, $"/api/admin/accounts/{other.Id}/temporary-password", new { version = other.Version });
        Assert.Equal(HttpStatusCode.Conflict, password.StatusCode);
    }

    [Fact]
    public async Task StaleVersionsAndMissingStatusAreRejected()
    {
        using var factory = new AuthApiFactory();
        using var admin = factory.NewClient();
        await factory.SeedAsync(admin: true, mustChangePassword: false);
        await Login(admin);
        var row = await AddUser(factory, "worker01");
        using var missing = await Post(admin, $"/api/admin/accounts/{row.Id}/status", new { version = row.Version });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var stale = await Post(admin, $"/api/admin/accounts/{row.Id}/status", new { isActive = false, version = "stale" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("account_conflict", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task PendingTemporaryPasswordCanBeReissuedAndThePreviousOneStopsWorking()
    {
        using var factory = new AuthApiFactory();
        using var admin = factory.NewClient();
        using var old = factory.NewClient();
        using var fresh = factory.NewClient();
        await factory.SeedAsync(admin: true, mustChangePassword: false);
        await Login(admin);
        var created = await Create(admin);
        await Login(old, created.Account.Username, created.TemporaryPassword);
        var row = (await admin.GetFromJsonAsync<AdminAccountPage>("/api/admin/accounts"))!.Items.Single(x => x.Id == created.Account.Id);
        using var response = await Post(admin, $"/api/admin/accounts/{row.Id}/temporary-password", new { version = row.Version });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var replaced = (await response.Content.ReadFromJsonAsync<AccountCredentials>())!;
        Assert.NotEqual(created.TemporaryPassword, replaced.TemporaryPassword);
        Assert.True(replaced.Account.MustChangePassword);
        using var oldSession = await old.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var stored = (await users.FindByIdAsync(row.Id.ToString()))!;
        Assert.False(await users.CheckPasswordAsync(stored, created.TemporaryPassword));
        await Login(fresh, row.Username, replaced.TemporaryPassword);
    }

    [Fact]
    public async Task CompletedFirstPasswordCannotBeReissuedAndListNeverContainsSecrets()
    {
        using var factory = new AuthApiFactory();
        using var admin = factory.NewClient();
        await factory.SeedAsync(admin: true, mustChangePassword: false);
        await Login(admin);
        var row = await AddUser(factory, "ready-worker", pending: false);
        using var response = await Post(admin, $"/api/admin/accounts/{row.Id}/temporary-password", new { version = row.Version });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("initial_password_completed", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
        using var list = await admin.GetAsync("/api/admin/accounts?page=1&pageSize=1");
        var body = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", body);
        Assert.DoesNotContain("securityStamp", body);
        Assert.DoesNotContain("temporaryPassword", body);
        var page = (await list.Content.ReadFromJsonAsync<AdminAccountPage>())!;
        Assert.Equal(2, page.Total);
        Assert.Single(page.Items);
    }

    private static async Task<AccountCredentials> Create(HttpClient admin)
    {
        using var response = await Post(admin, "/api/admin/accounts", new { username = "worker01" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccountCredentials>())!;
    }

    private static async Task<AdminAccountResponse> AddUser(AuthApiFactory factory, string name, bool admin = false, bool pending = true)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = name, MustChangePassword = pending };
        Assert.True((await users.CreateAsync(user, AuthApiFactory.InitialPassword)).Succeeded);
        var role = admin ? AuthSetup.AdminRole : AuthSetup.UserRole;
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return new(user.Id, user.UserName!, user.IsActive, user.MustChangePassword, user.CreatedAtUtc, user.ConcurrencyStamp!, [role]);
    }

    private static async Task Login(HttpClient client, string username = AuthApiFactory.Username,
        string password = AuthApiFactory.InitialPassword)
    {
        using var response = await Post(client, "/api/auth/login", new LoginRequest(username, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, object body)
    {
        var token = (await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf"))!.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(AuthSetup.CsrfHeader, token);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }
}