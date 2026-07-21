using Archivio.Domain;
using Microsoft.EntityFrameworkCore;

namespace Archivio.Persistence;

public sealed class ArchivioDbContext(DbContextOptions<ArchivioDbContext> options) : DbContext(options)
{
    public DbSet<SystemRecord> SystemRecords => Set<SystemRecord>();

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
    }
}
