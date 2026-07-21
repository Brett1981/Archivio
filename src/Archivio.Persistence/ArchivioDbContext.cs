using Archivio.Domain;
using Microsoft.EntityFrameworkCore;

namespace Archivio.Persistence;

public sealed class ArchivioDbContext(DbContextOptions<ArchivioDbContext> options) : DbContext(options)
{
    public DbSet<SystemRecord> SystemRecords => Set<SystemRecord>();
    public DbSet<LibrarySource> LibrarySources => Set<LibrarySource>();

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
    }
}
