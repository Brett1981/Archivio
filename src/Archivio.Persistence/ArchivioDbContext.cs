using Archivio.Domain;
using Microsoft.EntityFrameworkCore;

namespace Archivio.Persistence;

public sealed class ArchivioDbContext(DbContextOptions<ArchivioDbContext> options) : DbContext(options)
{
    public DbSet<SystemRecord> SystemRecords => Set<SystemRecord>();
    public DbSet<LibrarySource> LibrarySources => Set<LibrarySource>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SystemRecord>(entity =>
        {
            entity.ToTable("SystemRecords");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Value).HasMaxLength(1024).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<LibrarySource>(entity =>
        {
            entity.ToTable("LibrarySources");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Path).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.Type).HasConversion<int>().IsRequired();
            entity.Property(x => x.IsEnabled).IsRequired();
            entity.Property(x => x.CreatedAtUtc).IsRequired();
            entity.Property(x => x.UpdatedAtUtc).IsRequired();
            entity.HasIndex(x => x.Path).IsUnique();
        });

        modelBuilder.Entity<MediaItem>(entity =>
        {
            entity.ToTable("MediaItems");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FullPath).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.RelativePath).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.FileName).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Extension).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ContentHash).HasMaxLength(128);
            entity.Property(x => x.SizeBytes).IsRequired();
            entity.Property(x => x.CreatedAtUtc).IsRequired();
            entity.Property(x => x.ModifiedAtUtc).IsRequired();
            entity.Property(x => x.LastScannedAtUtc).IsRequired();
            entity.Property(x => x.IsMissing).IsRequired();
            entity.HasIndex(x => new { x.LibrarySourceId, x.FullPath }).IsUnique();
            entity.HasIndex(x => new { x.LibrarySourceId, x.RelativePath });
            entity.HasOne(x => x.LibrarySource)
                .WithMany()
                .HasForeignKey(x => x.LibrarySourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
