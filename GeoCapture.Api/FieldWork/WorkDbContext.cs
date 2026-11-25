using Microsoft.EntityFrameworkCore;

namespace GeoCapture.Api.FieldWork;

public sealed class FieldWorkRecord
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string OwnerName { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int Revision { get; set; }
    public Guid Version { get; set; }
}

public sealed class FieldWorkRevision
{
    public Guid Id { get; set; }
    public Guid WorkId { get; set; }
    public FieldWorkRecord Work { get; set; } = null!;
    public int Number { get; set; }
    public Guid ChangeId { get; set; }
    public Guid ResultVersion { get; set; }
    public Guid ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public string Reason { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public string DocumentJson { get; set; } = "";
    public string ChangesJson { get; set; } = "[]";
}

public sealed class FieldWorkPhoto
{
    public Guid Id { get; set; }
    public Guid WorkId { get; set; }
    public FieldWorkRecord Work { get; set; } = null!;
    public string DescriptorJson { get; set; } = "";
    public string OriginalMimeType { get; set; } = "";
    public byte[] OriginalBytes { get; set; } = [];
    public byte[] StampedBytes { get; set; } = [];
    public string OriginalHash { get; set; } = "";
    public string StampedHash { get; set; } = "";
}

public sealed class FieldWorkSettings
{
    public int Id { get; set; } = 1;
    public bool AllowQrGeneration { get; set; }
    public Guid Version { get; set; }
    public Guid ChangedBy { get; set; }
    public DateTime ChangedAtUtc { get; set; }
}

public sealed class WorkDbContext(DbContextOptions<WorkDbContext> options) : DbContext(options)
{
    public DbSet<FieldWorkRecord> Records => Set<FieldWorkRecord>();
    public DbSet<FieldWorkRevision> Revisions => Set<FieldWorkRevision>();
    public DbSet<FieldWorkPhoto> Photos => Set<FieldWorkPhoto>();
    public DbSet<FieldWorkSettings> Settings => Set<FieldWorkSettings>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("geo_capture");
        builder.Entity<FieldWorkRecord>(entity =>
        {
            entity.ToTable("FieldWorks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OwnerName).HasMaxLength(256);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OwnerId, x.UpdatedAtUtc });
        });
        builder.Entity<FieldWorkRevision>(entity =>
        {
            entity.ToTable("FieldWorkRevisions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ActorName).HasMaxLength(256);
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.Property(x => x.PayloadHash).HasMaxLength(64);
            entity.HasOne(x => x.Work).WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.WorkId, x.Number }).IsUnique();
            entity.HasIndex(x => new { x.WorkId, x.ChangeId }).IsUnique();
        });
        builder.Entity<FieldWorkPhoto>(entity =>
        {
            entity.ToTable("FieldWorkPhotos");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OriginalMimeType).HasMaxLength(64);
            entity.Property(x => x.OriginalHash).HasMaxLength(64);
            entity.Property(x => x.StampedHash).HasMaxLength(64);
            entity.HasOne(x => x.Work).WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<FieldWorkSettings>(entity =>
        {
            entity.ToTable("FieldWorkSettings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Version).IsConcurrencyToken();
        });
    }
}