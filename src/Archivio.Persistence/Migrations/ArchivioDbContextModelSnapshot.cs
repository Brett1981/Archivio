using Archivio.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Archivio.Persistence.Migrations;

[DbContext(typeof(ArchivioDbContext))]
public partial class ArchivioDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");

        modelBuilder.Entity<LibrarySource>(entity =>
        {
            entity.Property(x => x.Id).HasColumnType("TEXT");
            entity.Property(x => x.CreatedAtUtc).HasColumnType("TEXT");
            entity.Property(x => x.IsEnabled).HasColumnType("INTEGER");
            entity.Property(x => x.Name).IsRequired().HasMaxLength(128).HasColumnType("TEXT");
            entity.Property(x => x.Path).IsRequired().HasMaxLength(1024).HasColumnType("TEXT");
            entity.Property(x => x.Type).HasConversion<int>().HasColumnType("INTEGER");
            entity.Property(x => x.UpdatedAtUtc).HasColumnType("TEXT");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Path).IsUnique();
            entity.ToTable("LibrarySources");
        });

        modelBuilder.Entity<MediaItem>(entity =>
        {
            entity.Property(x => x.Id).HasColumnType("TEXT");
            entity.Property(x => x.ContentHash).HasMaxLength(128).HasColumnType("TEXT");
            entity.Property(x => x.CreatedAtUtc).HasColumnType("TEXT");
            entity.Property(x => x.Extension).IsRequired().HasMaxLength(32).HasColumnType("TEXT");
            entity.Property(x => x.FileName).IsRequired().HasMaxLength(512).HasColumnType("TEXT");
            entity.Property(x => x.FullPath).IsRequired().HasMaxLength(2048).HasColumnType("TEXT");
            entity.Property(x => x.IsMissing).HasColumnType("INTEGER");
            entity.Property(x => x.LastScannedAtUtc).HasColumnType("TEXT");
            entity.Property(x => x.LibrarySourceId).HasColumnType("TEXT");
            entity.Property(x => x.ModifiedAtUtc).HasColumnType("TEXT");
            entity.Property(x => x.RelativePath).IsRequired().HasMaxLength(2048).HasColumnType("TEXT");
            entity.Property(x => x.SizeBytes).HasColumnType("INTEGER");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.LibrarySourceId, x.FullPath }).IsUnique();
            entity.HasIndex(x => new { x.LibrarySourceId, x.RelativePath });
            entity.ToTable("MediaItems");
        });

        modelBuilder.Entity<SystemRecord>(entity =>
        {
            entity.Property(x => x.Id).HasColumnType("TEXT");
            entity.Property(x => x.CreatedAtUtc).HasColumnType("TEXT");
            entity.Property(x => x.Name).IsRequired().HasMaxLength(128).HasColumnType("TEXT");
            entity.Property(x => x.Value).IsRequired().HasMaxLength(1024).HasColumnType("TEXT");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.ToTable("SystemRecords");
        });

        modelBuilder.Entity<MediaItem>()
            .HasOne(x => x.LibrarySource)
            .WithMany()
            .HasForeignKey(x => x.LibrarySourceId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();
    }
}
