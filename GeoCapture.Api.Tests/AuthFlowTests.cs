using System.Net;
using System.Net.Http.Json;
using GeoCapture.Api.Auth;
using Xunit;

namespace GeoCapture.Api.Tests;

public sealed class AuthFlowTests
{
    [Fact]
    public async Task AnonymousAccessAndMissingCsrfAreRejected()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync();

        await AssertGet(client, "/api/account", HttpStatusCode.Unauthorized);

        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(
                AuthApiFactory.Username,
                AuthApiFactory.InitialPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FirstLoginIsRestrictedEvenForAnAdmin()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(admin: true);

        await Login(client);

        var user = await client.GetFromJsonAsync<UserResponse>("/api/auth/me");
        Assert.True(user!.MustChangePassword);

        await AssertGet(client, "/api/account", HttpStatusCode.Forbidden);
        await AssertGet(client, "/api/admin/users", HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PasswordChangeSurvivesLogoutAndAnotherLogin()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync();

        await Login(client);

        using var changed = await Post(
            client,
            "/api/auth/password",
            new ChangePasswordRequest(
                AuthApiFactory.InitialPassword,
                AuthApiFactory.NewPassword));

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        await AssertGet(client, "/api/account", HttpStatusCode.OK);

        using var logout = await Post(client, "/api/auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        await AssertGet(client, "/api/auth/me", HttpStatusCode.Unauthorized);

        await Login(client, AuthApiFactory.NewPassword);

        var user = await client.GetFromJsonAsync<UserResponse>("/api/auth/me");
        Assert.False(user!.MustChangePassword);

        await AssertGet(client, "/api/account", HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(AuthApiFactory.InitialPassword, AuthApiFactory.InitialPassword)]
    [InlineData(AuthApiFactory.InitialPassword, "short")]
    [InlineData("wrong-current", AuthApiFactory.NewPassword)]
    public async Task InvalidChangeKeepsTheAccountRestricted(
        string currentPassword,
        string newPassword)
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync();

        await Login(client);

        using var response = await Post(
            client,
            "/api/auth/password",
            new ChangePasswordRequest(currentPassword, newPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var user = await client.GetFromJsonAsync<UserResponse>("/api/auth/me");
        Assert.True(user!.MustChangePassword);

        await AssertGet(client, "/api/account", HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NormalUserCannotReadTheAdminUserList()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);

        await Login(client);

        await AssertGet(client, "/api/account", HttpStatusCode.OK);
        await AssertGet(client, "/api/admin/users", HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PasswordChangeInvalidatesOlderCookies()
    {
        using var factory = new AuthApiFactory();
        using var first = factory.NewClient();
        using var second = factory.NewClient();
        await factory.SeedAsync();

        await Login(first);
        await Login(second);

        using var response = await Post(
            first,
            "/api/auth/password",
            new ChangePasswordRequest(
                AuthApiFactory.InitialPassword,
                AuthApiFactory.NewPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await AssertGet(first, "/api/account", HttpStatusCode.OK);
        await AssertGet(second, "/api/auth/me", HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DisabledUserCannotUseAnExistingSession()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);

        await Login(client);
        await factory.DisableUserAsync();

        await AssertGet(client, "/api/auth/me", HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FiveFailedAttemptsLockTheAccount()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await Post(
                client,
                "/api/auth/login",
                new LoginRequest(AuthApiFactory.Username, "wrong-password"));

            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using var correct = await Post(
            client,
            "/api/auth/login",
            new LoginRequest(
                AuthApiFactory.Username,
                AuthApiFactory.InitialPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);
    }

    [Fact]
    public async Task RegistrationEndpointDoesNotExist()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);

        await Login(client);

        using var response = await Post(
            client,
            "/api/auth/register",
            new { username = "new-user", password = "ExamplePassword927" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task Login(
        HttpClient client,
        string password = AuthApiFactory.InitialPassword)
    {
        using var response = await Post(
            client,
            "/api/auth/login",
            new LoginRequest(AuthApiFactory.Username, password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> Post(
        HttpClient client,
        string path,
        object body)
    {
        var csrf = await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf");

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };

        request.Headers.Add(AuthSetup.CsrfHeader, csrf!.Token);

        return await client.SendAsync(request);
    }

    private static async Task AssertGet(
        HttpClient client,
        string path,
        HttpStatusCode expected)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
    }
}