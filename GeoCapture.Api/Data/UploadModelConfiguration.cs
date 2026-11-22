using GeoCapture.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace GeoCapture.Api.Data;

public static class UploadModelConfiguration
{
    public static void ConfigurePhotoUploads(this ModelBuilder builder)
    {
        builder.Entity<PhotoUpload>(upload =>
        {
            upload.ToTable("PhotoUploads");
            upload.HasKey(x => x.Id);
            upload.Property(x => x.SerialNumber).HasMaxLength(1024).IsRequired();
            upload.Property(x => x.OldSerialNumber).HasMaxLength(1024);
            upload.Property(x => x.UploadedByUsername).HasMaxLength(256).IsRequired();
            upload.Property(x => x.PayloadSha256).HasMaxLength(64).IsRequired();
            upload.Property(x => x.Version).IsConcurrencyToken();
            upload.Property(x => x.UploadedAtUtc).HasConversion(
                value => value,
                value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
            upload.Property(x => x.ModifiedAtUtc).HasConversion(
                value => value,
                value => value.HasValue
                    ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
                    : (DateTime?)null);
            upload.HasIndex(x => new { x.OwnerId, x.ClientUploadId }).IsUnique();
            upload.HasIndex(x => new { x.OwnerId, x.UploadedAtUtc, x.Id });
            upload.HasOne(x => x.Owner).WithMany()
                .HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            upload.HasMany(x => x.Photos).WithOne(x => x.Upload)
                .HasForeignKey(x => x.UploadId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CapturedPhoto>(photo =>
        {
            photo.ToTable("CapturedPhotos");
            photo.HasKey(x => x.Id);
            photo.Property(x => x.CoordinatesText).HasMaxLength(40).IsRequired();
            photo.Property(x => x.OriginalMimeType).HasMaxLength(32).IsRequired();
            photo.Property(x => x.OriginalSha256).HasMaxLength(64).IsRequired();
            photo.Property(x => x.StampedSha256).HasMaxLength(64).IsRequired();
            photo.Property(x => x.CapturedAtUtc).HasConversion(
                value => value,
                value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
            photo.Property(x => x.LocationMeasuredAtUtc).HasConversion(
                value => value,
                value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
            photo.HasIndex(x => new { x.UploadId, x.ClientPhotoId }).IsUnique();
            photo.HasIndex(x => new { x.UploadId, x.Position }).IsUnique();
            photo.HasOne(x => x.Content).WithOne(x => x.Photo)
                .HasForeignKey<PhotoContent>(x => x.PhotoId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PhotoContent>(content =>
        {
            content.ToTable("PhotoContents");
            content.HasKey(x => x.PhotoId);
            content.Property(x => x.OriginalBytes).IsRequired();
            content.Property(x => x.StampedBytes).IsRequired();
        });
    }
}