using System.Net;
using System.Net.Http.Json;
using GeoCapture.Api.Auth;
using Xunit;

namespace GeoCapture.Api.Tests;

public sealed class InitialPasswordTests
{
    [Fact]
    public async Task AnonymousUserCannotSetAPassword()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync();

        using var response = await Post(
            client,
            "/api/auth/initial-password",
            new InitialPasswordRequest(AuthApiFactory.NewPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InitialPasswordRequiresCsrf()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync();
        await Login(client);

        using var response = await client.PostAsJsonAsync(
            "/api/auth/initial-password",
            new InitialPasswordRequest(AuthApiFactory.NewPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NewPasswordAloneCompletesTheFirstChange()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync();
        await Login(client);

        using var response = await Post(
            client,
            "/api/auth/initial-password",
            new InitialPasswordRequest(AuthApiFactory.NewPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<UserResponse>();
        Assert.False(user!.MustChangePassword);

        using var account = await client.GetAsync("/api/account");
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);

        using var logout = await Post(client, "/api/auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var oldLogin = await Post(
            client,
            "/api/auth/login",
            new LoginRequest(
                AuthApiFactory.Username,
                AuthApiFactory.InitialPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        await Login(client, AuthApiFactory.NewPassword);
        user = await client.GetFromJsonAsync<UserResponse>("/api/auth/me");
        Assert.False(user!.MustChangePassword);
    }

    [Theory]
    [InlineData("short")]
    [InlineData(AuthApiFactory.InitialPassword)]
    public async Task InvalidPasswordKeepsTheFirstChangeRequired(string password)
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync();
        await Login(client);

        using var response = await Post(
            client,
            "/api/auth/initial-password",
            new InitialPasswordRequest(password));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var user = await client.GetFromJsonAsync<UserResponse>("/api/auth/me");
        Assert.True(user!.MustChangePassword);

        using var account = await client.GetAsync("/api/account");
        Assert.Equal(HttpStatusCode.Forbidden, account.StatusCode);
    }

    [Fact]
    public async Task ACompletedAccountCannotUseTheInitialPasswordEndpoint()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.NewClient();
        await factory.SeedAsync(mustChangePassword: false);
        await Login(client);

        using var response = await Post(
            client,
            "/api/auth/initial-password",
            new InitialPasswordRequest(AuthApiFactory.NewPassword));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InitialPasswordChangeRevokesOlderSessions()
    {
        using var factory = new AuthApiFactory();
        using var first = factory.NewClient();
        using var second = factory.NewClient();
        await factory.SeedAsync();
        await Login(first);
        await Login(second);

        using var response = await Post(
            first,
            "/api/auth/initial-password",
            new InitialPasswordRequest(AuthApiFactory.NewPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var oldSession = await second.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
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
}