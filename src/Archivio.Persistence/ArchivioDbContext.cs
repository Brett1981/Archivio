using Archivio.Domain;
using Microsoft.EntityFrameworkCore;

namespace Archivio.Persistence;

public sealed class ArchivioDbContext(DbContextOptions<ArchivioDbContext> options) : DbContext(options)
{
    public DbSet<SystemRecord> SystemRecords => Set<SystemRecord>();
    public DbSet<LibrarySource> LibrarySources => Set<LibrarySource>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    internal DbSet<AudiobookAnalysisRunEntity> AudiobookAnalysisRuns => Set<AudiobookAnalysisRunEntity>();
    internal DbSet<AudiobookMetadataCacheEntity> AudiobookMetadataCache => Set<AudiobookMetadataCacheEntity>();
    internal DbSet<AudiobookCandidateSnapshotEntity> AudiobookCandidateSnapshots => Set<AudiobookCandidateSnapshotEntity>();
    internal DbSet<OnlineMetadataCacheEntity> OnlineMetadataCache => Set<OnlineMetadataCacheEntity>();
    internal DbSet<AudiobookOrganisationProposalEntity> AudiobookOrganisationProposals =>
        Set<AudiobookOrganisationProposalEntity>();
    internal DbSet<AudiobookBatchDecisionEntity> AudiobookBatchDecisions =>
        Set<AudiobookBatchDecisionEntity>();

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

        modelBuilder.Entity<AudiobookAnalysisRunEntity>(entity =>
        {
            entity.ToTable("AudiobookAnalysisRuns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<int>().IsRequired();
            entity.Property(x => x.StartedAtUtc).IsRequired();
            entity.Property(x => x.UpdatedAtUtc).IsRequired();
            entity.Property(x => x.ProcessedCount).IsRequired();
            entity.Property(x => x.TotalCount).IsRequired();
            entity.Property(x => x.WarningCount).IsRequired();
            entity.Property(x => x.CandidateCount).IsRequired();
            entity.Property(x => x.ErrorMessage).HasMaxLength(2048);
            entity.HasIndex(x => x.LibrarySourceId).IsUnique();
            entity.HasOne<LibrarySource>()
                .WithMany()
                .HasForeignKey(x => x.LibrarySourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AudiobookMetadataCacheEntity>(entity =>
        {
            entity.ToTable("AudiobookMetadataCache");
            entity.HasKey(x => x.MediaItemId);
            entity.Property(x => x.SizeBytes).IsRequired();
            entity.Property(x => x.ModifiedAtUtc).IsRequired();
            entity.Property(x => x.AnalysedAtUtc).IsRequired();
            entity.Property(x => x.MetadataJson).IsRequired();
            entity.HasIndex(x => x.LibrarySourceId);
            entity.HasOne<MediaItem>()
                .WithOne()
                .HasForeignKey<AudiobookMetadataCacheEntity>(x => x.MediaItemId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<LibrarySource>()
                .WithMany()
                .HasForeignKey(x => x.LibrarySourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AudiobookCandidateSnapshotEntity>(entity =>
        {
            entity.ToTable("AudiobookCandidateSnapshots");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SortOrder).IsRequired();
            entity.Property(x => x.CandidateJson).IsRequired();
            entity.HasIndex(x => new { x.AnalysisRunId, x.SortOrder }).IsUnique();
            entity.HasOne<AudiobookAnalysisRunEntity>()
                .WithMany()
                .HasForeignKey(x => x.AnalysisRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OnlineMetadataCacheEntity>(entity =>
        {
            entity.ToTable("OnlineMetadataCache");
            entity.HasKey(x => x.CandidateKey);
            entity.Property(x => x.CandidateKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.InputSignature).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RetrievedAtUtc).IsRequired();
            entity.Property(x => x.SuggestionJson);
            entity.HasIndex(x => x.LibrarySourceId);
            entity.HasOne<LibrarySource>()
                .WithMany()
                .HasForeignKey(x => x.LibrarySourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AudiobookOrganisationProposalEntity>(entity =>
        {
            entity.ToTable("AudiobookOrganisationProposals");
            entity.HasKey(x => x.CandidateKey);
            entity.Property(x => x.CandidateKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.InputSignature).HasMaxLength(64).IsRequired();
            entity.Property(x => x.GeneratedAtUtc).IsRequired();
            entity.Property(x => x.ProposalJson).IsRequired();
            entity.HasIndex(x => x.LibrarySourceId);
            entity.HasOne<LibrarySource>()
                .WithMany()
                .HasForeignKey(x => x.LibrarySourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AudiobookBatchDecisionEntity>(entity =>
        {
            entity.ToTable("AudiobookBatchDecisions");
            entity.HasKey(x => new { x.LibrarySourceId, x.PlanKey });
            entity.Property(x => x.PlanKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.InputSignature).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Decision).IsRequired();
            entity.Property(x => x.UpdatedAtUtc).IsRequired();
            entity.HasOne<LibrarySource>()
                .WithMany()
                .HasForeignKey(x => x.LibrarySourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
