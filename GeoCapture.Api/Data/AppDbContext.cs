using GeoCapture.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GeoCapture.Api.Data;

public sealed class AppDbContext
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>
{
    public const string Schema = "geo_capture";

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(Schema);

        builder.Entity<AppUser>(user =>
        {
            user.Property(x => x.MustChangePassword).HasDefaultValue(true);
            user.Property(x => x.IsActive).HasDefaultValue(true);
        });
    }
}