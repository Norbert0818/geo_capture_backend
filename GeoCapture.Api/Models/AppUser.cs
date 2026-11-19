using Microsoft.AspNetCore.Identity;

namespace GeoCapture.Api.Models;

public sealed class AppUser : IdentityUser<Guid>
{
    public AppUser()
    {
        Id = Guid.NewGuid();
    }

    public bool MustChangePassword { get; set; } = true;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? PasswordChangedAtUtc { get; set; }
}