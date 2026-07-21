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
    }
}
