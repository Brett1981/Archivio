using Archivio.Application.Abstractions;
using Archivio.Application.Services;
using Archivio.Domain;

namespace Archivio.UnitTests;

public sealed class AudiobookBatchPlanningServiceTests
{
    [Fact]
    public async Task PrepareBatch_CreatesAReadOnlyFileLevelOperation()
    {
        var fixture = CreateCandidate("plan-1", "Author", "Book", ready: true);
        var store = new StubDecisionStore();
        var service = new AudiobookBatchPlanningService(store);

        var result = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            [fixture.Item]);

        var plan = Assert.IsType<AudiobookBatchPlan>(Assert.Single(result).BatchPlan);
        var operation = Assert.Single(plan.Operations);
        Assert.Equal(AudiobookBatchValidationStatus.Ready, plan.ValidationStatus);
        Assert.Equal(AudiobookBatchDecision.Approved, plan.Decision);
        Assert.Equal(fixture.Item.RelativePath, operation.SourceRelativePath);
        Assert.Equal(
            Path.Combine("Author", "Book", "Author - Book.mp3"),
            operation.DestinationRelativePath);
        Assert.Equal(AudiobookFileOperationKind.MoveAndRename, operation.Kind);
        Assert.Equal(fixture.Item.FullPath, fixture.Candidate.Parts[0].MediaItem.FullPath);
    }

    [Fact]
    public async Task PrepareBatch_BlocksPlansThatShareADestination()
    {
        var first = CreateCandidate("plan-1", "Author", "Book", ready: true, sourceFile: "First.mp3");
        var second = CreateCandidate(
            "plan-2",
            "Author",
            "Book",
            ready: true,
            sourceFile: "Second.mp3",
            sourceId: first.SourceId,
            root: first.Root);
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            first.SourceId,
            first.Root,
            [first.Candidate, second.Candidate],
            [first.Item, second.Item]);

        Assert.All(result, candidate =>
        {
            Assert.Equal(AudiobookBatchValidationStatus.Conflict, candidate.BatchPlan?.ValidationStatus);
            Assert.Contains(
                candidate.BatchPlan!.Warnings,
                warning => warning.Contains("same destination", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task ApprovedDecision_RoundTripsWhenThePreviewIsUnchanged()
    {
        var fixture = CreateCandidate("plan-1", "Author", "Book", ready: true);
        var store = new StubDecisionStore();
        var service = new AudiobookBatchPlanningService(store);
        var prepared = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            [fixture.Item]);

        var approved = await service.SetDecisionAsync(
            fixture.SourceId,
            prepared,
            ["plan-1"],
            AudiobookBatchDecision.Approved);
        var restored = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            approved,
            [fixture.Item]);

        Assert.Equal(AudiobookBatchDecision.Approved, Assert.Single(restored).BatchPlan?.Decision);
    }

    [Fact]
    public async Task ReviewRequiredPlan_CannotBeApproved()
    {
        var fixture = CreateCandidate("plan-1", "Author", "Book", ready: false);
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());
        var prepared = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            [fixture.Item]);

        var result = await service.SetDecisionAsync(
            fixture.SourceId,
            prepared,
            ["plan-1"],
            AudiobookBatchDecision.Approved);

        var plan = Assert.IsType<AudiobookBatchPlan>(Assert.Single(result).BatchPlan);
        Assert.Equal(AudiobookBatchValidationStatus.ReviewRequired, plan.ValidationStatus);
        Assert.Equal(AudiobookBatchDecision.Pending, plan.Decision);
    }

    [Fact]
    public async Task PrepareBatch_LeavesLowConfidencePlanPendingForReview()
    {
        var fixture = CreateCandidate("plan-1", "Author", "Book", ready: true);
        var candidate = fixture.Candidate with
        {
            Confidence = 0.70m,
            Warnings = ["Identity confidence is low."],
            OrganisationProposal = fixture.Candidate.OrganisationProposal! with
            {
                ReadyForAutomaticHandling = false,
                Warnings = ["Author or title needs review."]
            }
        };
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [candidate],
            [fixture.Item]);

        var plan = Assert.IsType<AudiobookBatchPlan>(Assert.Single(result).BatchPlan);
        Assert.Equal(AudiobookBatchValidationStatus.ReviewRequired, plan.ValidationStatus);
        Assert.Equal(AudiobookBatchDecision.Pending, plan.Decision);
    }

    [Fact]
    public async Task PrepareBatch_AutoApprovesPlanWhoseLowConfidenceEvidenceWasResolvedOnline()
    {
        var fixture = CreateCandidate("plan-1", "Author", "Book", ready: true);
        var candidate = fixture.Candidate with
        {
            Confidence = 0.70m,
            OrganisationProposal = fixture.Candidate.OrganisationProposal! with
            {
                UsesOnlineMetadata = true
            }
        };
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [candidate],
            [fixture.Item]);

        var plan = Assert.IsType<AudiobookBatchPlan>(Assert.Single(result).BatchPlan);
        Assert.True(candidate.NeedsReview);
        Assert.Equal(AudiobookBatchValidationStatus.Ready, plan.ValidationStatus);
        Assert.Equal(AudiobookBatchDecision.Approved, plan.Decision);
    }

    [Fact]
    public async Task PrepareBatch_PreservesExplicitDeferralForAutomationSafePlan()
    {
        var fixture = CreateCandidate("plan-1", "Author", "Book", ready: true);
        var store = new StubDecisionStore();
        var service = new AudiobookBatchPlanningService(store);
        var prepared = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            [fixture.Item]);
        var deferred = await service.SetDecisionAsync(
            fixture.SourceId,
            prepared,
            ["plan-1"],
            AudiobookBatchDecision.Deferred);

        var restored = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            deferred,
            [fixture.Item]);

        Assert.Equal(AudiobookBatchDecision.Deferred, Assert.Single(restored).BatchPlan?.Decision);
    }

    [Fact]
    public async Task PrepareBatch_BlocksAnOccupiedIndexedDestination()
    {
        var fixture = CreateCandidate("plan-1", "Author", "Book", ready: true);
        var now = DateTime.UtcNow;
        var targetRelativePath = Path.Combine("Author", "Book", "Author - Book.mp3");
        var occupant = new MediaItem(
            fixture.SourceId,
            Path.Combine(fixture.Root, targetRelativePath),
            targetRelativePath,
            200,
            now,
            now,
            now);
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            [fixture.Item, occupant]);

        var plan = Assert.IsType<AudiobookBatchPlan>(Assert.Single(result).BatchPlan);
        Assert.Equal(AudiobookBatchValidationStatus.Conflict, plan.ValidationStatus);
        Assert.Contains(
            plan.Warnings,
            warning => warning.Contains("already occupied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareBatch_IgnoresMissingHistoricalItemAtDestination()
    {
        var fixture = CreateCandidate("plan-1", "Author", "Book", ready: true);
        var now = DateTime.UtcNow;
        var targetRelativePath = Path.Combine("Author", "Book", "Author - Book.mp3");
        var historicalItem = new MediaItem(
            fixture.SourceId,
            Path.Combine(fixture.Root, targetRelativePath),
            targetRelativePath,
            200,
            now,
            now,
            now);
        historicalItem.MarkMissing(now.AddMinutes(1));
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            [fixture.Item, historicalItem]);

        var plan = Assert.IsType<AudiobookBatchPlan>(Assert.Single(result).BatchPlan);
        Assert.Equal(AudiobookBatchValidationStatus.Ready, plan.ValidationStatus);
        Assert.DoesNotContain(
            plan.Warnings,
            warning => warning.Contains("already occupied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareBatch_AssignsStableSequenceNamesToGroupedFiles()
    {
        var first = CreateCandidate("plan-1", "Author", "Book", ready: true, sourceFile: "Part 01.mp3");
        var second = CreateCandidate(
            "plan-1",
            "Author",
            "Book",
            ready: true,
            sourceFile: "Part 02.mp3",
            sourceId: first.SourceId,
            root: first.Root);
        var primaryProposal = first.Candidate.OrganisationProposal! with
        {
            RelatedCandidateCount = 2,
            SourceFileCount = 2,
            SuggestedFileNamePattern = "001 - Book{original extension}"
        };
        var candidates = new[]
        {
            first.Candidate with { OrganisationProposal = primaryProposal },
            second.Candidate with
            {
                OrganisationProposal = primaryProposal with { IsPrimaryCandidate = false }
            }
        };
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            first.SourceId,
            first.Root,
            candidates,
            [first.Item, second.Item]);

        var plan = Assert.IsType<AudiobookBatchPlan>(result[0].BatchPlan);
        Assert.Collection(
            plan.Operations,
            operation => Assert.EndsWith(
                Path.Combine("Author", "Book", "001 - Book.mp3"),
                operation.DestinationRelativePath,
                StringComparison.Ordinal),
            operation => Assert.EndsWith(
                Path.Combine("Author", "Book", "002 - Book.mp3"),
                operation.DestinationRelativePath,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareBatch_UsesDistinctEmbeddedTrackNumbersBeforeAlphabeticalPaths()
    {
        var first = CreateCandidate(
            "plan-1",
            "Author",
            "Book",
            ready: true,
            sourceFile: "Zebra.mp3",
            trackNumber: 1);
        var second = CreateCandidate(
            "plan-1",
            "Author",
            "Book",
            ready: true,
            sourceFile: "Alpha.mp3",
            sourceId: first.SourceId,
            root: first.Root,
            trackNumber: 2);
        var primaryProposal = first.Candidate.OrganisationProposal! with
        {
            RelatedCandidateCount = 2,
            SourceFileCount = 2,
            SuggestedFileNamePattern = "001 - Book{original extension}"
        };
        var candidates = new[]
        {
            first.Candidate with { OrganisationProposal = primaryProposal },
            second.Candidate with
            {
                OrganisationProposal = primaryProposal with { IsPrimaryCandidate = false }
            }
        };
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            first.SourceId,
            first.Root,
            candidates,
            [first.Item, second.Item]);

        var plan = Assert.IsType<AudiobookBatchPlan>(result[0].BatchPlan);
        Assert.Collection(
            plan.Operations,
            operation =>
            {
                Assert.EndsWith("Zebra.mp3", operation.SourceRelativePath, StringComparison.Ordinal);
                Assert.EndsWith("001 - Book.mp3", operation.DestinationRelativePath, StringComparison.Ordinal);
            },
            operation =>
            {
                Assert.EndsWith("Alpha.mp3", operation.SourceRelativePath, StringComparison.Ordinal);
                Assert.EndsWith("002 - Book.mp3", operation.DestinationRelativePath, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task PrepareBatch_AcceptsCompleteTrailingFilenameSequenceForConsolidatedTracks()
    {
        var first = CreateCandidate(
            "plan-1",
            "Roald Dahl",
            "The BFG",
            ready: true,
            sourceFile: "(Roald Dahl) The BFG - 01.mp3");
        var second = CreateCandidate(
            "plan-1",
            "Roald Dahl",
            "The BFG",
            ready: true,
            sourceFile: "(Roald Dahl) The BFG - 02.mp3",
            sourceId: first.SourceId,
            root: first.Root);
        var primaryProposal = first.Candidate.OrganisationProposal! with
        {
            RecommendedAction = AudiobookOrganisationAction.ConsolidateCandidates,
            RelatedCandidateCount = 2,
            SourceFileCount = 2,
            SuggestedFileNamePattern = "001 - The BFG{original extension}"
        };
        var candidates = new[]
        {
            first.Candidate with
            {
                Parts = [first.Candidate.Parts[0] with { SequenceWasInferred = false }],
                OrganisationProposal = primaryProposal
            },
            second.Candidate with
            {
                Parts = [second.Candidate.Parts[0] with { SequenceWasInferred = false }],
                OrganisationProposal = primaryProposal with { IsPrimaryCandidate = false }
            }
        };
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            first.SourceId,
            first.Root,
            candidates,
            [first.Item, second.Item]);

        var plan = Assert.IsType<AudiobookBatchPlan>(result[0].BatchPlan);
        Assert.Equal(AudiobookBatchValidationStatus.Ready, plan.ValidationStatus);
        Assert.Empty(plan.Warnings);
        Assert.Collection(
            plan.Operations,
            operation => Assert.EndsWith("001 - The BFG.mp3", operation.DestinationRelativePath),
            operation => Assert.EndsWith("002 - The BFG.mp3", operation.DestinationRelativePath));
    }

    [Fact]
    public async Task PrepareBatch_BlocksConsolidationWithoutACompleteTrackSequence()
    {
        var first = CreateCandidate(
            "plan-1",
            "Author",
            "Book",
            ready: true,
            sourceFile: "First.mp3",
            trackNumber: 10);
        var second = CreateCandidate(
            "plan-1",
            "Author",
            "Book",
            ready: true,
            sourceFile: "Duplicate.mp3",
            sourceId: first.SourceId,
            root: first.Root,
            trackNumber: 10);
        var primaryProposal = first.Candidate.OrganisationProposal! with
        {
            RecommendedAction = AudiobookOrganisationAction.ConsolidateCandidates,
            RelatedCandidateCount = 2,
            SourceFileCount = 2,
            SuggestedFileNamePattern = "001 - Book{original extension}"
        };
        var candidates = new[]
        {
            first.Candidate with { OrganisationProposal = primaryProposal },
            second.Candidate with
            {
                OrganisationProposal = primaryProposal with { IsPrimaryCandidate = false }
            }
        };
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            first.SourceId,
            first.Root,
            candidates,
            [first.Item, second.Item]);

        var plan = Assert.IsType<AudiobookBatchPlan>(result[0].BatchPlan);
        Assert.Equal(AudiobookBatchValidationStatus.Conflict, plan.ValidationStatus);
        Assert.Contains(
            plan.Warnings,
            warning => warning.Contains("complete, unique track sequence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareBatch_CreatesMetadataOnlyOperationForCanonicalFileWithStaleTags()
    {
        var fixture = CreateCanonicalCandidate(tagsMatch: false);
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            [fixture.Item]);

        var plan = Assert.IsType<AudiobookBatchPlan>(Assert.Single(result).BatchPlan);
        Assert.Equal(AudiobookBatchValidationStatus.Ready, plan.ValidationStatus);
        Assert.Equal(AudiobookFileOperationKind.UpdateMetadata, Assert.Single(plan.Operations).Kind);
    }

    [Fact]
    public async Task PrepareBatch_ReportsNoChangeForCanonicalFileWithMatchingTags()
    {
        var fixture = CreateCanonicalCandidate(tagsMatch: true);
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            [fixture.Item]);

        var plan = Assert.IsType<AudiobookBatchPlan>(Assert.Single(result).BatchPlan);
        Assert.Equal(AudiobookBatchValidationStatus.NoChange, plan.ValidationStatus);
        Assert.Equal(AudiobookFileOperationKind.NoChange, Assert.Single(plan.Operations).Kind);
    }

    [Fact]
    public async Task PrepareBatch_MovesCanonicalFileWhenDestinationRootDiffers()
    {
        var fixture = CreateCanonicalCandidate(tagsMatch: true);
        var destinationRoot = Path.Combine(
            Path.GetTempPath(),
            "Archivio.Destination.Tests",
            Guid.NewGuid().ToString("N"));
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            destinationRoot,
            [fixture.Candidate],
            [fixture.Item]);

        var plan = Assert.IsType<AudiobookBatchPlan>(Assert.Single(result).BatchPlan);
        var operation = Assert.Single(plan.Operations);
        Assert.Equal(AudiobookBatchValidationStatus.Ready, plan.ValidationStatus);
        Assert.Equal(AudiobookFileOperationKind.MoveAndRename, operation.Kind);
        Assert.Equal(fixture.Item.RelativePath, operation.SourceRelativePath);
        Assert.Equal(fixture.Item.RelativePath, operation.DestinationRelativePath);
    }

    [Fact]
    public async Task PrepareBatch_BlocksExistingFileInSeparateDestination()
    {
        var fixture = CreateCanonicalCandidate(tagsMatch: true);
        var destinationRoot = Path.Combine(
            Path.GetTempPath(),
            "Archivio.Destination.Tests",
            Guid.NewGuid().ToString("N"));
        var occupiedPath = Path.Combine(destinationRoot, fixture.Item.RelativePath);
        var service = new AudiobookBatchPlanningService(
            new StubDecisionStore(),
            new OccupiedDestinationFileOperator(occupiedPath));

        var result = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            destinationRoot,
            [fixture.Candidate],
            [fixture.Item]);

        var plan = Assert.IsType<AudiobookBatchPlan>(Assert.Single(result).BatchPlan);
        Assert.Equal(AudiobookBatchValidationStatus.Conflict, plan.ValidationStatus);
        Assert.Contains(
            plan.Warnings,
            warning => warning.Contains("will not be overwritten", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareBatch_DoesNotReplaceExistingTagsWithUnavailableOptionalMetadata()
    {
        var fixture = CreateCanonicalCandidate(tagsMatch: true);
        var candidate = fixture.Candidate with
        {
            OrganisationProposal = fixture.Candidate.OrganisationProposal! with
            {
                FirstPublishedYear = null,
                GenreCategory = "Uncategorised"
            }
        };
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [candidate],
            [fixture.Item]);

        var plan = Assert.IsType<AudiobookBatchPlan>(Assert.Single(result).BatchPlan);
        Assert.Equal(AudiobookBatchValidationStatus.NoChange, plan.ValidationStatus);
        Assert.Equal(AudiobookFileOperationKind.NoChange, Assert.Single(plan.Operations).Kind);
    }

    private static CandidateFixture CreateCanonicalCandidate(bool tagsMatch)
    {
        var sourceId = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "Archivio.Tests", Guid.NewGuid().ToString("N"));
        var relativePath = Path.Combine("Fiction", "Author", "Book (2026)", "Author - Book.mp3");
        var now = DateTime.UtcNow;
        var item = new MediaItem(
            sourceId,
            Path.Combine(root, relativePath),
            relativePath,
            100,
            now,
            now,
            now);
        var metadata = new LocalMediaMetadata(
            item.FullPath,
            new MetadataValue(tagsMatch ? "Book" : "Wrong title", MetadataValueSource.EmbeddedTag),
            new MetadataValue(tagsMatch ? "Author" : "Wrong author", MetadataValueSource.EmbeddedTag),
            new MetadataValue(tagsMatch ? "Book" : "Wrong album", MetadataValueSource.EmbeddedTag),
            new MetadataValue(tagsMatch ? "Fiction" : "Wrong genre", MetadataValueSource.EmbeddedTag),
            tagsMatch ? 2026u : 1999u,
            1,
            null,
            null,
            null,
            null,
            null,
            false,
            [],
            [],
            1,
            null);
        var proposal = new AudiobookOrganisationProposal(
            "canonical-plan",
            "Author",
            "Book",
            2026,
            "Fiction",
            Path.Combine("Fiction", "Author", "Book (2026)"),
            "Author - Book.mp3",
            AudiobookOrganisationAction.Keep,
            1,
            1,
            true,
            false,
            1m,
            true,
            false,
            [],
            [],
            now);
        var candidate = new AudiobookCandidateGroup(
            "Author - Book",
            "Author",
            "Book",
            MetadataValueSource.FolderStructure,
            MetadataValueSource.FolderStructure,
            false,
            [new AudiobookCandidatePart(item, 1, false, metadata)],
            1m,
            [])
        {
            OrganisationProposal = proposal
        };
        return new CandidateFixture(sourceId, root, item, candidate);
    }

    private static CandidateFixture CreateCandidate(
        string planKey,
        string author,
        string title,
        bool ready,
        string sourceFile = "Original.mp3",
        Guid? sourceId = null,
        string? root = null,
        uint? trackNumber = null)
    {
        var librarySourceId = sourceId ?? Guid.NewGuid();
        var libraryRoot = root ?? Path.Combine(
            Path.GetTempPath(),
            "Archivio.Tests",
            Guid.NewGuid().ToString("N"));
        var now = DateTime.UtcNow;
        var relativePath = Path.Combine("Incoming", sourceFile);
        var item = new MediaItem(
            librarySourceId,
            Path.Combine(libraryRoot, relativePath),
            relativePath,
            100,
            now,
            now,
            now);
        var metadata = new LocalMediaMetadata(
            item.FullPath,
            new MetadataValue(title, MetadataValueSource.EmbeddedTag),
            new MetadataValue(author, MetadataValueSource.EmbeddedTag),
            new MetadataValue(null, MetadataValueSource.None),
            new MetadataValue(null, MetadataValueSource.None),
            null,
            trackNumber,
            null,
            null,
            null,
            null,
            null,
            false,
            [],
            []);
        var proposal = new AudiobookOrganisationProposal(
            planKey,
            author,
            title,
            null,
            "Uncategorised",
            Path.Combine(author, title),
            $"{author} - {title}.mp3",
            AudiobookOrganisationAction.MoveAndRename,
            1,
            1,
            true,
            false,
            ready ? 1m : 0.60m,
            ready,
            false,
            [],
            ready ? [] : ["Review required."],
            now);
        var candidate = new AudiobookCandidateGroup(
            $"{author} - {title}",
            author,
            title,
            MetadataValueSource.EmbeddedTag,
            MetadataValueSource.EmbeddedTag,
            true,
            [new AudiobookCandidatePart(item, 1, true, metadata)],
            ready ? 1m : 0.60m,
            ready ? [] : ["Review required."])
        {
            OrganisationProposal = proposal
        };
        return new CandidateFixture(librarySourceId, libraryRoot, item, candidate);
    }

    private sealed record CandidateFixture(
        Guid SourceId,
        string Root,
        MediaItem Item,
        AudiobookCandidateGroup Candidate);

    private sealed class StubDecisionStore : IAudiobookBatchDecisionStore
    {
        private readonly Dictionary<string, AudiobookBatchDecisionEntry> _entries =
            new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, AudiobookBatchDecisionEntry>> LoadAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, AudiobookBatchDecisionEntry>>(_entries);

        public Task SaveAsync(
            Guid librarySourceId,
            IReadOnlyCollection<AudiobookBatchDecisionEntry> entries,
            CancellationToken cancellationToken = default)
        {
            foreach (var entry in entries)
            {
                _entries[entry.PlanKey] = entry;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class OccupiedDestinationFileOperator(string occupiedPath) : IAudiobookFileOperator
    {
        public bool FileExists(string path) =>
            string.Equals(path, occupiedPath, StringComparison.OrdinalIgnoreCase);

        public bool DirectoryExists(string path) => true;
        public AudiobookFileSnapshot GetSnapshot(string path) => throw new NotSupportedException();
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public void WriteAllBytesNew(string path, ReadOnlySpan<byte> data) => throw new NotSupportedException();
        public void Move(string sourcePath, string destinationPath) => throw new NotSupportedException();
    }
}
