namespace GeoCapture.Api.Auth;

public sealed record LoginRequest(string Username, string Password);

public sealed record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);

public sealed record UserResponse(
    Guid Id,
    string Username,
    bool MustChangePassword,
    string[] Roles);

public sealed record CsrfResponse(string Token);

public sealed record ApiError(string Code, string[]? Details = null);