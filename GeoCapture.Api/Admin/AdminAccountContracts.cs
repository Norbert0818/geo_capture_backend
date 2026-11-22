namespace GeoCapture.Api.Admin;

public sealed record CreateAccountRequest(string? Username);

public sealed record AccountStatusRequest(
    bool? IsActive,
    string? Version);

public sealed record TemporaryPasswordRequest(string? Version);

public sealed record AdminAccountResponse(
    Guid Id,
    string Username,
    bool IsActive,
    bool MustChangePassword,
    DateTimeOffset CreatedAtUtc,
    string Version,
    string[] Roles);

public sealed record AdminAccountPage(
    int Page,
    int PageSize,
    int Total,
    AdminAccountResponse[] Items);

public sealed record AccountCredentials(
    AdminAccountResponse Account,
    string TemporaryPassword);